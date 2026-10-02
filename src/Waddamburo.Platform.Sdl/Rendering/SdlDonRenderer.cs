using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using Bgfx;
using Waddamburo.Formats.Don;
using Waddamburo.Formats.Nud;
using Waddamburo.Formats.Nut;

namespace Waddamburo.Platform.Sdl.Rendering;

public enum DonCameraLayout { Standard, Gameplay, Retry, RetrySuccess, Balloon }

/// <summary>Shared-device Don renderer. All methods must run on the owning SDL thread.</summary>
public sealed unsafe class SdlDonRenderer : IDisposable, IGpuRenderPrepass
{
    // Stage units covered by each player's target: menus use 600x600, gameplay the game's 448x256 slot.
    private readonly (float Width, float Height)[] _targetSizes = [(600, 600), (600, 600), (600, 600)];
    private const int PaletteSize = 40;
    private const string ShaderPrefix = "Waddamburo.Shaders.";
    private const float DonNear = 256;
    private const float DonFar = 4096;
    // Independently reconstructed camera from observed gameplay projection geometry.
    private static readonly Matrix4x4 GameplayCamera =
        Matrix4x4.CreateLookAt(new(-430, 22, 1045), new(6.6f, 15.6f, 0), Vector3.UnitY)
        * Matrix4x4.CreatePerspectiveFieldOfView(2 * MathF.PI / 180, 448f / 256, DonNear, DonFar);
    // The revival drum is viewed from below and nearly head-on in its square surface.
    private static readonly Matrix4x4 RetryCamera =
        Matrix4x4.CreateLookAt(new(178, -643, 2634), new(0, 12, 0), Vector3.UnitY)
        * Matrix4x4.CreatePerspectiveFieldOfView(2 * MathF.PI / 180, 1, DonNear, DonFar);
    private static readonly Matrix4x4 RetrySuccessCamera =
        Matrix4x4.CreateLookAt(new(-839, 43, 1568), new(0, 19, 0), Vector3.UnitY)
        * Matrix4x4.CreatePerspectiveFieldOfView(2 * MathF.PI / 180, 1, DonNear, DonFar);
    private static readonly Matrix4x4 PlayerOneCamera = withDonDepth(new Matrix4x4(
        50.5310f, 0.4728f, 0.5763f, 0.4715f,
        0f, 57.2952f, -0.0214f, -0.0175f,
        27.0253f, -0.8839f, -1.0776f, -0.8817f,
        0.0008f, -866.3033f, 1675.5881f, 2280.0266f));
    // Balloon view (traced, mode 3): the menu camera moved closer and lower, Don ~9% larger.
    private static readonly Matrix4x4 BalloonCamera = withDonDepth(new Matrix4x4(
        50.5310f, 0.4896f, 0.5763f, 0.4715f,
        0f, 57.2945f, -0.0221f, -0.0181f,
        27.0253f, -0.9155f, -1.0776f, -0.8817f,
        0.0010f, -721.9110f, 1452.6600f, 2097.6300f));

    private static Matrix4x4 withDonDepth(Matrix4x4 camera)
    {
        // The menu camera is stored as a combined matrix. Replace only its depth
        // column; keep its horizontal/vertical projection and clip W unchanged.
        var scale = DonFar / (DonFar - DonNear);
        camera.M13 = scale * camera.M14;
        camera.M23 = scale * camera.M24;
        camera.M33 = scale * camera.M34;
        camera.M43 = scale * camera.M44 - DonNear * scale;
        return camera;
    }

    private readonly RenderDevice _compositor;
    private readonly string _assetRoot;
    private readonly DonAnimationFile _bindAnimation;
    private readonly DonSkeleton _skeleton;
    private readonly ImmutableArray<Matrix4x4> _bindWorld;
    private readonly Dictionary<string, DonAnimationFile> _animations = new(StringComparer.Ordinal);
    // Per player: the costume's meshes and face atlas, shared through the caches below.
    private readonly List<GpuMesh>[] _meshes = [[], [], []];
    private readonly Dictionary<uint, bgfx.TextureHandle>[] _faceTextures = [[], [], []];
    private readonly Dictionary<string, List<GpuMesh>> _modelCache = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Path, bool Recolor), Dictionary<uint, bgfx.TextureHandle>> _faceCache = [];
    private readonly List<bgfx.TextureHandle> _ownedTextures = [];
    private readonly List<GpuMesh> _ownedMeshes = [];
    // Slots 0 and 1: the players; 2: the entry's card dialog Don (donExM), drawn only while shown.
    private readonly Player[] _players = [new(), new(), new()];
    private readonly float[] _pose = new float[DonSkeleton.CharacterValuesPerFrame];

    /// <summary>Display-rate position between the last two ticks (0 = previous, 1 = latest).</summary>
    public float Interpolation { get; set; } = 1;
    private readonly Target[] _targets = new Target[3];
    private readonly float[] _poseUniforms = new float[16 * (PaletteSize + 1)];
    private readonly bgfx.ProgramHandle _modelProgram;
    private readonly bgfx.ProgramHandle _postProgram;
    private readonly bgfx.VertexBufferHandle _postTriangle;
    private readonly bgfx.UniformHandle _materialSampler;
    private readonly bgfx.UniformHandle _characterSampler;
    private readonly bgfx.UniformHandle _cameraUniform;
    private readonly bgfx.UniformHandle _bonesUniform;
    private readonly bgfx.UniformHandle _outlineUniform;
    private readonly bgfx.UniformHandle _materialUniform;
    private readonly bgfx.UniformHandle _replaceRedUniform;
    private readonly bgfx.UniformHandle _replaceGreenUniform;
    private readonly bgfx.UniformHandle _replaceBlueUniform;
    private readonly bgfx.UniformHandle _postUniform;
    private bgfx.VertexLayout _meshLayout;
    private readonly bgfx.TextureHandle _whiteTexture;
    private bool _mirrorPlayerTwoCamera = true;
    // Per player: in two-player gameplay one Don can be in a balloon while the other plays on.
    private readonly DonCameraLayout[] _layouts = new DonCameraLayout[3];

    // A player's own colours (face, body, limb as 0xRRGGBB); null: Don-chan's, or Katsu-chan's in slot 1.
    private readonly (uint Face, uint Body, uint Limb)?[] _colors = new (uint, uint, uint)?[3];

    /// <summary>Colours a Don: the texture's green channel is the face, red the body, blue the limbs.</summary>
    public void SetColors(int playerIndex, (uint Face, uint Body, uint Limb)? colors)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(playerIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(playerIndex, _colors.Length);
        _colors[playerIndex] = colors;
    }

    /// <summary>Draw slot 2 (the entry's card dialog Don).</summary>
    public bool DialogVisible { get; set; }
    private bool _disposed;

    internal SdlDonRenderer(RenderDevice compositor, string assetRoot)
    {
        _compositor = compositor;
        _assetRoot = Path.GetFullPath(assetRoot ?? throw new ArgumentNullException(nameof(assetRoot)));
        if (!Directory.Exists(_assetRoot))
            throw new DirectoryNotFoundException($"Don asset root does not exist: {_assetRoot}");

        _bindAnimation = readAnimation("ani/don_bind.bin");
        // Every motion up front (~80 files, ~4 MB): reading one as it first played stalled that frame
        // (Go-Go, fever and combo motions mid-song). Files of another shape are not motions.
        foreach (var file in Directory.EnumerateFiles(Path.Combine(_assetRoot, "ani"), "*.bin"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name == "don_bind")
                continue;
            try
            {
                loadMotion(name);
            }
            catch (InvalidDataException)
            {
            }
        }
        _skeleton = DonSkeleton.ForAnimation(_bindAnimation);
        _bindWorld = _skeleton.EvaluateWorld(_bindAnimation.GetFrame(0));
        _modelProgram = BgfxSupport.LoadProgram("vs_don", "fs_don");
        _postProgram = BgfxSupport.LoadProgram("vs_don_post", "fs_don_post");
        _materialSampler = bgfx.create_uniform("s_material", bgfx.UniformType.Sampler, 1);
        _characterSampler = bgfx.create_uniform("s_character", bgfx.UniformType.Sampler, 1);
        _cameraUniform = bgfx.create_uniform("u_camera", bgfx.UniformType.Mat4, 1);
        _bonesUniform = bgfx.create_uniform("u_bones", bgfx.UniformType.Mat4, PaletteSize);
        _outlineUniform = bgfx.create_uniform("u_outline", bgfx.UniformType.Vec4, 1);
        _materialUniform = bgfx.create_uniform("u_material", bgfx.UniformType.Vec4, 1);
        _replaceRedUniform = bgfx.create_uniform("u_replaceRed", bgfx.UniformType.Vec4, 1);
        _replaceGreenUniform = bgfx.create_uniform("u_replaceGreen", bgfx.UniformType.Vec4, 1);
        _replaceBlueUniform = bgfx.create_uniform("u_replaceBlue", bgfx.UniformType.Vec4, 1);
        _postUniform = bgfx.create_uniform("u_post", bgfx.UniformType.Vec4, 1);
        fixed (bgfx.VertexLayout* layout = &_meshLayout)
        {
            bgfx.vertex_layout_begin(layout, bgfx.RendererType.Noop);
            bgfx.vertex_layout_add(layout, bgfx.Attrib.Position, 3, bgfx.AttribType.Float, false, false);
            bgfx.vertex_layout_add(layout, bgfx.Attrib.Normal, 3, bgfx.AttribType.Float, false, false);
            bgfx.vertex_layout_add(layout, bgfx.Attrib.TexCoord0, 2, bgfx.AttribType.Float, false, false);
            bgfx.vertex_layout_add(layout, bgfx.Attrib.Color0, 4, bgfx.AttribType.Float, false, false);
            bgfx.vertex_layout_add(layout, bgfx.Attrib.Weight, 4, bgfx.AttribType.Float, false, false);
            bgfx.vertex_layout_add(layout, bgfx.Attrib.Indices, 4, bgfx.AttribType.Float, false, false);
            bgfx.vertex_layout_end(layout);
        }
        var postLayout = new bgfx.VertexLayout();
        bgfx.vertex_layout_begin(&postLayout, bgfx.RendererType.Noop);
        bgfx.vertex_layout_add(&postLayout, bgfx.Attrib.Position, 2, bgfx.AttribType.Float, false, false);
        bgfx.vertex_layout_end(&postLayout);
        ReadOnlySpan<float> triangle = [-1, -1, 3, -1, -1, 3];
        _postTriangle = bgfx.create_vertex_buffer(BgfxSupport.Copy(MemoryMarshal.AsBytes(triangle)), &postLayout, 0);
        _whiteTexture = BgfxSupport.CreateRgba8(1, 1, [255, 255, 255, 255]);
        _ownedTextures.Add(_whiteTexture);
        SetCostume(0, null, 0, 0, 0);
        SetCostume(1, null, 0, 0, 0);
        SetCostume(2, null, 0, 0, 0);
        for (var index = 0; index < _targets.Length; index++)
            _targets[index] = createTarget((600, 600));
        var idle = loadMotion("don_select_loop");
        foreach (var player in _players)
            player.Set(idle, idle);
        _compositor.AddPrepass(this);
    }

    public RenderTextureId GetTexture(int playerIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(playerIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(playerIndex, _targets.Length);
        return _targets[playerIndex].TextureId;
    }

    public void Reset(bool mirrorPlayerTwoCamera, DonCameraLayout layout = DonCameraLayout.Standard)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _mirrorPlayerTwoCamera = mirrorPlayerTwoCamera;
        SetCameraLayout(layout);
        var idle = loadMotion("don_select_loop");
        foreach (var player in _players)
            player.Set(idle, idle);
    }

    public void SetCameraLayout(DonCameraLayout layout)
    {
        for (var index = 0; index < _players.Length; index++)
            SetCameraLayout(index, layout);
    }

    /// <summary>Whether the second player's menu-style views use the reflected camera.</summary>
    public bool MirrorPlayerTwoCamera
    {
        get => _mirrorPlayerTwoCamera;
        set => _mirrorPlayerTwoCamera = value;
    }

    public void SetCameraLayout(int playerIndex, DonCameraLayout layout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(playerIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(playerIndex, _players.Length);
        _layouts[playerIndex] = layout;
        _targetSizes[playerIndex] = layout == DonCameraLayout.Gameplay ? (448, 256) : (600, 600);
    }

    public void SetMotion(int playerIndex, string? oneShot, string? loop)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(playerIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(playerIndex, _players.Length);
        var player = _players[playerIndex];
        var nextLoop = loop is null ? null : loadMotion(normalizeMotion(loop));
        if (oneShot is not null)
        {
            player.Set(loadMotion(normalizeMotion(oneShot)), nextLoop);
            return;
        }
        if (nextLoop is not null)
            player.Set(nextLoop, nextLoop);
    }

    public void SetIdle(int playerIndex, string loop)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(playerIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(playerIndex, _players.Length);
        _players[playerIndex].SetIdle(loadMotion(normalizeMotion(loop)));
    }

    public void Advance(double frames = 1)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!double.IsFinite(frames) || frames < 0)
            throw new ArgumentOutOfRangeException(nameof(frames));
        foreach (var player in _players)
            player.Advance(frames);
    }

    void IGpuRenderPrepass.Record(uint width, uint height)
    {
        // Render at twice the window's stage scale and let the composite's downscale anti-alias.
        var scale = Math.Min(width / 1280f, height / 720f);
        uint pixels(float units) => (uint)Math.Clamp(MathF.Round(units * scale * 2 / 8) * 8, 64, 4096);
        for (var index = 0; index < _players.Length; index++)
        {
            if (index == 2 && !DialogVisible)
                continue;
            resizeTarget(index, (pixels(_targetSizes[index].Width), pixels(_targetSizes[index].Height)));
            recordPlayer(index);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _compositor.RemovePrepass(this);
        foreach (var target in _targets)
            if (target.TextureId.Value != 0)
                _compositor.UnregisterBorrowedTexture(target.TextureId);
        releaseResources();
        _disposed = true;
    }

    private void recordPlayer(int playerIndex)
    {
        var player = _players[playerIndex];
        var target = _targets[playerIndex];
        player.Sample(Interpolation, _pose, _skeleton.ExpressionOffset);
        ReadOnlySpan<float> frame = _pose;
        var animatedWorld = _skeleton.EvaluateWorld(frame);
        var matrices = MemoryMarshal.Cast<float, Matrix4x4>(_poseUniforms.AsSpan());
        var targetSize = _targetSizes[playerIndex];
        var camera = _layouts[playerIndex] switch
        {
            DonCameraLayout.Gameplay => GameplayCamera,
            DonCameraLayout.Retry => RetryCamera,
            DonCameraLayout.RetrySuccess => RetrySuccessCamera,
            DonCameraLayout.Balloon => BalloonCamera,
            _ => PlayerOneCamera,
        };
        // Both gameplay lanes face the same way; only menu scenes stand the second player opposite.
        var reflectedCamera = playerIndex == 1 && _mirrorPlayerTwoCamera && _layouts[playerIndex] != DonCameraLayout.Gameplay;
        if (reflectedCamera)
        {
            camera.M11 = -camera.M11;
            camera.M21 = -camera.M21;
            camera.M31 = -camera.M31;
            camera.M41 = -camera.M41;
        }
        matrices[0] = camera;
        for (var index = 0; index < PaletteSize; index++)
        {
            if (index < animatedWorld.Length && Matrix4x4.Invert(_bindWorld[index], out var inverseBind))
                matrices[index + 1] = inverseBind * animatedWorld[index];
            else
                matrices[index + 1] = Matrix4x4.Identity;
        }

        var modelView = (ushort)(playerIndex * 2);
        var postView = (ushort)(modelView + 1);
        var (pixelWidth, pixelHeight) = ((ushort)target.Pixels.Width, (ushort)target.Pixels.Height);
        bgfx.set_view_frame_buffer(modelView, target.Model);
        bgfx.set_view_rect(modelView, 0, 0, pixelWidth, pixelHeight, 0, 1);
        bgfx.set_view_clear(modelView, (ushort)(bgfx.ClearFlags.Color | bgfx.ClearFlags.Depth), 0, 1, 0);
        // Submission order is draw order: opaque first, then the blended decals.
        bgfx.set_view_mode(modelView, bgfx.ViewMode.Sequential);
        bgfx.touch(modelView);

        var expression = _skeleton.GetExpression(frame);
        var face = _faceTextures[playerIndex].TryGetValue((uint)expression, out var faceTexture) ? faceTexture : _whiteTexture;
        var replaceRed = _colors[playerIndex] is { } own ? rgb(own.Body)
            : playerIndex != 1 ? color(0x6C, 0xC3, 0xC6) : color(0xF9, 0x4C, 0x2C);
        var replaceGreen = _colors[playerIndex] is { } ownFace ? rgb(ownFace.Face)
            : playerIndex != 1 ? color(0xF9, 0x4C, 0x2C) : color(0x6C, 0xC3, 0xC6);
        var replaceBlue = _colors[playerIndex] is { } ownLimb ? rgb(ownLimb.Limb) : color(0xF8, 0xF0, 0xDC);
        foreach (var blended in new[] { false, true })
        {
            foreach (var mesh in _meshes[playerIndex])
            {
                foreach (var material in mesh.Materials)
                {
                    if ((material.SourceBlend != 0) != blended)
                        continue;
                    var kind = material.ShaderKind;
                    // Kind 8 is a head back-face pass, not the normal-mesh inverted hull.
                    // The silhouette post-pass supplies its visible outer edge.
                    if (kind == 8)
                        continue;
                    // bgfx keeps uniforms per draw, so every draw carries its pose.
                    fixed (float* pose = _poseUniforms)
                    {
                        bgfx.set_uniform(_cameraUniform, pose, 1);
                        bgfx.set_uniform(_bonesUniform, pose + 16, PaletteSize);
                    }
                    // Hull line: 3 stage pixels, offset per axis (clip units per stage pixel).
                    var outline = new Float4(kind is 3 or 8 ? 3f : 0, 2f / targetSize.Width, 2f / targetSize.Height, 0);
                    bgfx.set_uniform(_outlineUniform, &outline, 1);
                    var parameters = new Float4(material.AlphaFunction != 0 || kind == 8
                        ? Math.Max(material.AlphaReference, (byte)6) / 255f
                        : 0, kind, 0, 0);
                    bgfx.set_uniform(_materialUniform, &parameters, 1);
                    bgfx.set_uniform(_replaceRedUniform, &replaceRed, 1);
                    bgfx.set_uniform(_replaceGreenUniform, &replaceGreen, 1);
                    bgfx.set_uniform(_replaceBlueUniform, &replaceBlue, 1);
                    // CCW-front culling; reflecting the 2P camera reverses projected winding, so
                    // swap the culled side to keep the authored visible side.
                    var cull = material.CullMode switch
                    {
                        0x405 => reflectedCamera ? bgfx.StateFlags.CullCcw : bgfx.StateFlags.CullCw,
                        0x404 => reflectedCamera ? bgfx.StateFlags.CullCw : bgfx.StateFlags.CullCcw,
                        _ => bgfx.StateFlags.None,
                    };
                    // Blended decals (face disc, costume prints) are tested against the opaque body so the
                    // ones on its far side stay hidden, but do not write depth.
                    var state = (ulong)(bgfx.StateFlags.WriteRgb | bgfx.StateFlags.WriteA | bgfx.StateFlags.DepthTestLequal | cull);
                    state |= blended
                        ? BgfxSupport.BlendSeparate(bgfx.StateFlags.BlendSrcAlpha, bgfx.StateFlags.BlendInvSrcAlpha,
                            bgfx.StateFlags.BlendOne, bgfx.StateFlags.BlendInvSrcAlpha)
                        : (ulong)bgfx.StateFlags.WriteZ;
                    bgfx.set_state(state, 0);
                    var texture = kind == 2
                        ? face
                        : material.TextureIds.Length > 0 && mesh.Textures.TryGetValue(material.TextureIds[0], out var materialTexture)
                            ? materialTexture
                            : _whiteTexture;
                    bgfx.set_texture(0, _materialSampler, texture,
                        kind == 1 ? (uint)(bgfx.SamplerFlags.MinPoint | bgfx.SamplerFlags.MagPoint | bgfx.SamplerFlags.MipPoint) : 0);
                    bgfx.set_vertex_buffer(0, mesh.VertexBuffer, 0, uint.MaxValue);
                    bgfx.set_index_buffer(mesh.IndexBuffer, 0, uint.MaxValue);
                    bgfx.submit(modelView, _modelProgram, 0, (byte)bgfx.DiscardFlags.All);
                }
            }
        }

        bgfx.set_view_frame_buffer(postView, target.Post);
        bgfx.set_view_rect(postView, 0, 0, pixelWidth, pixelHeight, 0, 1);
        bgfx.set_state((ulong)(bgfx.StateFlags.WriteRgb | bgfx.StateFlags.WriteA), 0);
        bgfx.set_texture(0, _characterSampler, bgfx.get_texture(target.Model, 0), uint.MaxValue);
        var postParameters = new Float4(3f * target.Pixels.Width / targetSize.Width,
            1f / target.Pixels.Width, 1f / target.Pixels.Height, 0);
        bgfx.set_uniform(_postUniform, &postParameters, 1);
        bgfx.set_vertex_buffer(0, _postTriangle, 0, 3);
        bgfx.submit(postView, _postProgram, 0, (byte)bgfx.DiscardFlags.All);
    }

    /// <summary>
    /// Dresses a player: a whole costume (full/cos/cos_NNN000 with its full/face atlas, or the
    /// default one) or divided parts (parts/head, parts/body, parts/paint). Player 2 uses an "r"
    /// face variant when one exists, otherwise the Katsu recolour (as the game looks them up).
    /// </summary>
    public void SetCostume(int playerIndex, int? whole, int head, int body, int paint)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(playerIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(playerIndex, _players.Length);
        var meshes = new List<GpuMesh>();
        string face;
        if (whole is { } costume)
        {
            meshes.AddRange(model($"full/cos/cos_{costume:000}000.nud"));
            face = assetExists($"full/face/face_{costume:000}000.nut")
                ? $"full/face/face_{costume:000}000.nut" : "full/face/face_000000.nut";
        }
        else
        {
            meshes.AddRange(model($"parts/body/body_{body:000}000.nud"));
            meshes.AddRange(model($"parts/head/head_{head:000}000.nud"));
            face = $"parts/paint/paint_{paint:000}000.nut";
        }
        var mirrored = Path.ChangeExtension(face, null) + "r.nut";
        var playerTwoVariant = playerIndex == 1 && assetExists(mirrored);
        _meshes[playerIndex] = meshes;
        _faceTextures[playerIndex] = faces(playerTwoVariant ? mirrored : face, recolor: playerIndex == 1 && !playerTwoVariant);
    }

    private bool assetExists(string relativePath) => File.Exists(Path.Combine(_assetRoot, relativePath));

    private List<GpuMesh> model(string relativePath)
    {
        if (!_modelCache.TryGetValue(relativePath, out var meshes))
            _modelCache[relativePath] = meshes = loadModel(relativePath);
        return meshes;
    }

    private Dictionary<uint, bgfx.TextureHandle> faces(string relativePath, bool recolor)
    {
        if (!_faceCache.TryGetValue((relativePath, recolor), out var textures))
            _faceCache[(relativePath, recolor)] = textures = loadFaces(relativePath, recolor);
        return textures;
    }

    private List<GpuMesh> loadModel(string relativePath)
    {
        var meshes = new List<GpuMesh>();
        var path = resolveAsset(relativePath);
        var model = NudFile.Parse(File.ReadAllBytes(path));
        var textures = uploadNut(Path.ChangeExtension(path, ".nut"));
        foreach (var item in model.Objects)
        {
            foreach (var polygon in item.Polygons)
            {
                if (polygon.TriangleIndices.IsEmpty)
                    continue;
                var vertices = polygon.Vertices.Select(static vertex => new GpuVertex(
                    vertex.Position,
                    vertex.Normal,
                    vertex.TextureCoordinate,
                    vertex.Color,
                    vertex.BoneWeights,
                    vertex.BoneIndices)).ToArray();
                fixed (bgfx.VertexLayout* layout = &_meshLayout)
                {
                    var mesh = new GpuMesh(
                        bgfx.create_vertex_buffer(BgfxSupport.Copy(MemoryMarshal.AsBytes(vertices.AsSpan())), layout, 0),
                        bgfx.create_index_buffer(BgfxSupport.Copy(MemoryMarshal.AsBytes(polygon.TriangleIndices.AsSpan())), 0),
                        polygon.Materials,
                        textures);
                    _ownedMeshes.Add(mesh);
                    meshes.Add(mesh);
                }
            }
        }
        return meshes;
    }

    private Dictionary<uint, bgfx.TextureHandle> loadFaces(string relativePath, bool replaceFaceColor)
    {
        var faces = new Dictionary<uint, bgfx.TextureHandle>();
        var nut = NutFile.Parse(File.ReadAllBytes(resolveAsset(relativePath)));
        foreach (var texture in nut.Textures)
        {
            var pixels = NutTextureDecoder.DecodeRgba8(texture);
            if (replaceFaceColor)
                replaceRgb(pixels, 0xF9, 0x4C, 0x2C, 0x6C, 0xC3, 0xC6);
            var id = texture.GlobalId ?? checked((uint)texture.Index);
            faces[id] = uploadRgba(texture.Width, texture.Height, pixels);
        }
        return faces;
    }

    private static void replaceRgb(
        Span<byte> pixels,
        byte sourceRed,
        byte sourceGreen,
        byte sourceBlue,
        byte targetRed,
        byte targetGreen,
        byte targetBlue)
    {
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            if (pixels[offset] != sourceRed
                || pixels[offset + 1] != sourceGreen
                || pixels[offset + 2] != sourceBlue)
            {
                continue;
            }
            pixels[offset] = targetRed;
            pixels[offset + 1] = targetGreen;
            pixels[offset + 2] = targetBlue;
        }
    }

    private Dictionary<uint, bgfx.TextureHandle> uploadNut(string path)
    {
        var result = new Dictionary<uint, bgfx.TextureHandle>();
        var nut = NutFile.Parse(File.ReadAllBytes(path));
        foreach (var texture in nut.Textures)
        {
            var pixels = NutTextureDecoder.DecodeRgba8(texture);
            result[texture.GlobalId ?? checked((uint)texture.Index)] = uploadRgba(texture.Width, texture.Height, pixels);
        }
        return result;
    }

    private DonAnimationFile loadMotion(string name)
    {
        if (_animations.TryGetValue(name, out var animation))
            return animation;
        animation = readAnimation($"ani/{name}.bin");
        if (animation.ValuesPerFrame != DonSkeleton.CharacterValuesPerFrame)
            throw new InvalidDataException($"Don motion '{name}' has stride {animation.ValuesPerFrame}, expected {DonSkeleton.CharacterValuesPerFrame}.");
        _animations.Add(name, animation);
        return animation;
    }

    private DonAnimationFile readAnimation(string relativePath) =>
        DonAnimationFile.Parse(File.ReadAllBytes(resolveAsset(relativePath)));

    private string resolveAsset(string relativePath)
    {
        var path = Path.GetFullPath(relativePath, _assetRoot);
        var relative = Path.GetRelativePath(_assetRoot, path);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new InvalidDataException($"Don asset path escapes its root: {relativePath}");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Don asset was not found: {relativePath}", path);
        return path;
    }

    private Target createTarget((uint Width, uint Height) pixels, RenderTextureId? reuse = null)
    {
        var (width, height) = ((ushort)pixels.Width, (ushort)pixels.Height);
        var attachments = stackalloc bgfx.TextureHandle[2];
        attachments[0] = bgfx.create_texture_2d(width, height, false, 1, bgfx.TextureFormat.RGBA8, (ulong)bgfx.TextureFlags.Rt, null, 0);
        attachments[1] = bgfx.create_texture_2d(width, height, false, 1, BgfxSupport.DepthFormat(), (ulong)bgfx.TextureFlags.RtWriteOnly, null, 0);
        var model = bgfx.create_frame_buffer_from_handles(2, attachments, true);
        var final = bgfx.create_texture_2d(width, height, false, 1, bgfx.TextureFormat.RGBA8, (ulong)bgfx.TextureFlags.Rt, null, 0);
        var post = bgfx.create_frame_buffer_from_handles(1, &final, true);
        if (!model.Valid || !post.Valid)
            throw new InvalidOperationException($"bgfx could not create a {width}x{height} Don target.");
        if (reuse is { } id)
            _compositor.ReplaceBorrowedTexture(id, final);
        return new Target(model, post, reuse ?? _compositor.RegisterBorrowedTexture(final), pixels);
    }

    private void resizeTarget(int index, (uint Width, uint Height) pixels)
    {
        var old = _targets[index];
        if (pixels == old.Pixels) return;
        _targets[index] = createTarget(pixels, old.TextureId);
        // bgfx defers the destruction until frames in flight no longer use them.
        bgfx.destroy_frame_buffer(old.Model);
        bgfx.destroy_frame_buffer(old.Post);
    }

    private bgfx.TextureHandle uploadRgba(uint width, uint height, ReadOnlySpan<byte> pixels)
    {
        var texture = BgfxSupport.CreateRgba8(width, height, pixels);
        _ownedTextures.Add(texture);
        return texture;
    }

    private void releaseResources()
    {
        foreach (var target in _targets)
        {
            if (target.Model.Valid) bgfx.destroy_frame_buffer(target.Model);
            if (target.Post.Valid) bgfx.destroy_frame_buffer(target.Post);
        }
        foreach (var mesh in _ownedMeshes)
        {
            bgfx.destroy_vertex_buffer(mesh.VertexBuffer);
            bgfx.destroy_index_buffer(mesh.IndexBuffer);
        }
        foreach (var texture in _ownedTextures)
            bgfx.destroy_texture(texture);
        _ownedMeshes.Clear();
        _ownedTextures.Clear();
        bgfx.destroy_vertex_buffer(_postTriangle);
        bgfx.destroy_program(_modelProgram);
        bgfx.destroy_program(_postProgram);
        foreach (var uniform in new[] { _materialSampler, _characterSampler, _cameraUniform, _bonesUniform, _outlineUniform,
            _materialUniform, _replaceRedUniform, _replaceGreenUniform, _replaceBlueUniform, _postUniform })
            bgfx.destroy_uniform(uniform);
    }

    private static string normalizeMotion(string value)
    {
        var normalized = value.ToLowerInvariant()
            .Replace("1p", "1P", StringComparison.Ordinal)
            .Replace("2p", "2P", StringComparison.Ordinal);
        return normalized == "don_siwng02" ? "don_swing02" : normalized;
    }

    private static Float4 color(byte red, byte green, byte blue) => new(red / 255f, green / 255f, blue / 255f, 1);

    private static Float4 rgb(uint value) => color((byte)(value >> 16), (byte)(value >> 8), (byte)value);

    private sealed class Player
    {
        public DonAnimationFile Motion { get; private set; } = null!;
        public DonAnimationFile? Loop { get; private set; }
        private double _frame;
        private double _lastStep; // frames advanced by the latest tick; 0 after a cut

        public void Set(DonAnimationFile motion, DonAnimationFile? loop)
        {
            Motion = motion;
            Loop = loop;
            _frame = 0;
            _lastStep = 0;
        }

        public void SetIdle(DonAnimationFile loop)
        {
            if (Motion != Loop) Loop = loop; // a one-shot is running: it ends into the new idle
            else if (Motion != loop) Set(loop, loop);
        }

        public void Advance(double frames)
        {
            _lastStep = frames;
            _frame += frames;
            if (_frame < Motion.FrameCount)
                return;
            if (Loop is not null)
            {
                _frame = (_frame - Motion.FrameCount) % Loop.FrameCount;
                Motion = Loop;
                return;
            }
            // Holding the final frame: report only the distance actually moved.
            var last = Motion.FrameCount - 1;
            _lastStep = Math.Max(0, last - (_frame - frames));
            _frame = last;
        }

        /// <summary>
        /// Writes the pose one tick behind the simulation, moved forward by the display
        /// interpolation, blending the two nearest baked frames. The expression is not blended.
        /// </summary>
        // ponytail: right after a one-shot hands over to its loop the lagged position clamps
        // to the loop's first frame for the rest of that tick.
        public void Sample(float interpolation, float[] pose, int? expressionOffset)
        {
            var position = Math.Max(0, _frame - _lastStep * (1 - interpolation));
            var index = Math.Min((int)position, Motion.FrameCount - 1);
            var weight = (float)Math.Min(1, position - index);
            var current = Motion.GetFrame(index);
            var next = index + 1 < Motion.FrameCount ? Motion.GetFrame(index + 1)
                : Loop == Motion ? Motion.GetFrame(0)
                : current;
            for (var i = 0; i < pose.Length; i++)
            {
                var delta = next[i] - current[i];
                // Baked Euler angles can wrap by a full turn between frames.
                if (MathF.Abs(MathF.Abs(delta) - MathF.Tau) < 0.5f)
                    delta -= MathF.CopySign(MathF.Tau, delta);
                pose[i] = current[i] + delta * weight;
            }
            if (expressionOffset is int offset)
                pose[offset] = current[offset];
        }
    }

    private sealed record GpuMesh(
        bgfx.VertexBufferHandle VertexBuffer,
        bgfx.IndexBufferHandle IndexBuffer,
        ImmutableArray<NudMaterial> Materials,
        Dictionary<uint, bgfx.TextureHandle> Textures);

    private readonly record struct Target(bgfx.FrameBufferHandle Model, bgfx.FrameBufferHandle Post,
        RenderTextureId TextureId, (uint Width, uint Height) Pixels);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct GpuVertex(
        Vector3 Position,
        Vector3 Normal,
        Vector2 TextureCoordinate,
        Vector4 Color,
        Vector4 Weights,
        Vector4 BoneIndices);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Float4(float X, float Y, float Z, float W);
}
