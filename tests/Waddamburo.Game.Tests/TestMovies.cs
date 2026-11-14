using System.Buffers.Binary;
using Waddamburo.Formats.Lmb;
using Waddamburo.Game.Don;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Tests;

/// <summary>Hand-built LMB movies for tests; compiled into the skin's tests too.</summary>
internal static class TestMovies
{
    public static LmbMovieDefinition TransitionMovie()
    {
        var action = new byte[]
        {
            0x96, 0x14, 0x00,
                0x07, 0x00, 0x00, 0x00, 0x00,
                0x07, 0x00, 0x00, 0x00, 0x00,
                0x07, 0x01, 0x00, 0x00, 0x00,
                0x07, 0x03, 0x00, 0x00, 0x00,
            0x96, 0x03, 0x00, 0x09, 0x00, 0x00,
            0x1C,
            0x96, 0x03, 0x00, 0x09, 0x01, 0x00,
            0x52,
            0x17,
            0x00,
        };
        var file = Lmb(
            Words(LmbTags.MovieProperties, 0, 0, 0, 7, 0, 0, 0, BitConverter.SingleToUInt32Bits(60)),
            Record(LmbTags.StringPool, StringPool("Lumen", "SetNextScene")),
            Record(LmbTags.ActionPool, ActionPool(action)),
            Words(LmbTags.DefineSprite, 7, 0, 0, 2, 3, 2, 0),
            Words(LmbTags.ShowFrame, 0, 1),
            Words(LmbTags.ShowFrame, 1, 2),
            Words(LmbTags.DoAction, 0, 0));
        return LmbSemanticReader.Read(file).Value;
    }

    public static LmbMovieDefinition DonMotionMovie()
    {
        var action = new byte[]
        {
            0x96, 0x08, 0x00,
                0x09, 0x02, 0x00,
                0x07, 0x01, 0x00, 0x00, 0x00,
            0x3C,
            0x96, 0x08, 0x00,
                0x09, 0x03, 0x00,
                0x07, 0x02, 0x00, 0x00, 0x00,
            0x3C,
            0x96, 0x14, 0x00,
                0x07, 0x02, 0x00, 0x00, 0x00,
                0x07, 0x01, 0x00, 0x00, 0x00,
                0x07, 0x00, 0x00, 0x00, 0x00,
                0x07, 0x03, 0x00, 0x00, 0x00,
            0x96, 0x03, 0x00, 0x09, 0x00, 0x00,
            0x1C,
            0x96, 0x03, 0x00, 0x09, 0x01, 0x00,
            0x52,
            0x17,
            0x00,
        };
        var file = Lmb(
            Words(LmbTags.MovieProperties, 0, 0, 0, 7, 0, 0, 0, BitConverter.SingleToUInt32Bits(60)),
            Record(LmbTags.StringPool, StringPool("Lumen", "SetMotion", "DON_ENTRY1P_OUT", "DON_ENTRY_LOOP")),
            Record(LmbTags.ActionPool, ActionPool(action)),
            Words(LmbTags.DefineSprite, 7, 0, 0, 2, 3, 2, 0),
            Words(LmbTags.ShowFrame, 0, 1),
            Words(LmbTags.ShowFrame, 1, 2),
            Words(LmbTags.DoAction, 0, 0));
        return LmbSemanticReader.Read(file).Value;
    }

    public static LmbMovieDefinition ResultDonMotionMovie(int side, int oneShot, int loop)
    {
        var action = new List<byte>();
        void pushNumber(int value)
        {
            action.AddRange([0x96, 0x05, 0x00, 0x07]);
            action.AddRange(BitConverter.GetBytes(value));
        }
        void pushString(int index) => action.AddRange([0x96, 0x03, 0x00, 0x09, (byte)index, 0x00]);
        pushNumber(loop);
        pushNumber(oneShot);
        pushNumber(side);
        pushString(4); // DonMot
        pushNumber(4);
        pushString(0); // flash
        action.Add(0x1C); // GetVariable
        pushString(1); // external
        action.Add(0x4E); // GetMember
        pushString(2); // ExternalInterface
        action.Add(0x4E); // GetMember
        pushString(3); // call
        action.AddRange([0x52, 0x17, 0x00]); // CallMethod, Pop, End
        var file = Lmb(
            Words(LmbTags.MovieProperties, 0, 0, 0, 7, 0, 0, 0, BitConverter.SingleToUInt32Bits(60)),
            Record(LmbTags.StringPool, StringPool("flash", "external", "ExternalInterface", "call", "DonMot")),
            Record(LmbTags.ActionPool, ActionPool([.. action])),
            Words(LmbTags.DefineSprite, 7, 0, 0, 2, 3, 2, 0),
            Words(LmbTags.ShowFrame, 0, 1),
            Words(LmbTags.ShowFrame, 1, 2),
            Words(LmbTags.DoAction, 0, 0));
        return LmbSemanticReader.Read(file).Value;
    }

    public static LmbFile Lmb(params (uint Tag, byte[] Payload)[] records)
    {
        using var stream = new MemoryStream();
        stream.Write("LMB\0"u8);
        stream.Write(new byte[LmbFile.HeaderLength - 4]);
        foreach (var (tag, payload) in records)
        {
            WriteUInt32(stream, tag);
            WriteUInt32(stream, checked((uint)payload.Length / 4));
            stream.Write(payload);
        }
        return LmbFile.Parse(stream.ToArray());
    }

    public static (uint Tag, byte[] Payload) Record(uint tag, byte[] payload) => (tag, payload);

    public static (uint Tag, byte[] Payload) Words(uint tag, params uint[] values)
    {
        var payload = new byte[values.Length * 4];
        for (var index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(index * 4, 4), values[index]);
        return (tag, payload);
    }

    public static byte[] ActionPool(byte[] action)
    {
        using var stream = new MemoryStream();
        WriteUInt32(stream, 1);
        WriteUInt32(stream, checked((uint)action.Length));
        stream.Write(action);
        while (stream.Position % 4 != 0)
            stream.WriteByte(0);
        return stream.ToArray();
    }

    public static byte[] StringPool(params string[] values)
    {
        using var stream = new MemoryStream();
        WriteUInt32(stream, checked((uint)values.Length));
        foreach (var value in values)
        {
            var encoded = System.Text.Encoding.UTF8.GetBytes(value);
            WriteUInt32(stream, checked((uint)encoded.Length));
            stream.Write(encoded);
            stream.WriteByte(0);
            while (stream.Position % 4 != 0)
                stream.WriteByte(0);
        }
        return stream.ToArray();
    }

    public static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }

    // Original synthetic callback movie: each exported callback forwards its name/argument to a recording host.
    public static LumenSceneLayer RecordingMovie(List<(string, double)> calls)
    {
        var names = new[] { "level01", "n", "Record", "flash", "external", "ExternalInterface", "addCallback",
            "Reset", "SetGekiRendaCount", "GekiRendaEnd", "SetRendaCount", "RendaEnd", "SetWidth", "HitAction", "OnUpdate",
            "AddNorma", "SetGogoTime", "SetCount", "EndResult", "SetPlayerNum", "SetWaiWai", "SetBranchAvailable", "SetGogo" };
        var code = new List<Avm1Instruction>();
        var offset = 0;
        Avm1Instruction op(byte opcode, Avm1Operand? operand = null, Avm1CodeBlock? body = null) =>
            new(opcode, offset++, 1, [], operand, null, body);
        Avm1Instruction push(params Avm1PushValue[] values) => op(0x96, new Avm1PushOperand([.. values]));
        Avm1PushStringValue str(int index) => new(8, (ushort)index);
        for (var index = 7; index < names.Length; index++)
        {
            var body = new Avm1CodeBlock(0, 6, [push(str(1)), op(0x1C),
                push(str(index), new Avm1PushIntegerValue(2), str(2)), op(0x3D), op(0x17), op(0)]);
            body = body with { Instructions = [.. body.Instructions.Select((instruction, i) => instruction with { Offset = i })] };
            code.Add(op(0x9B, new Avm1FunctionOperand((ushort)index, 0, 0, [new(null, 1)], 6), body));
            code.Add(push(str(index))); code.Add(op(0x1C));
            code.Add(push(new Avm1PushNullValue(), str(index), new Avm1PushIntegerValue(3), str(3)));
            code.Add(op(0x1C)); code.Add(push(str(4))); code.Add(op(0x4E));
            code.Add(push(str(5))); code.Add(op(0x4E)); code.Add(push(str(6))); code.Add(op(0x52)); code.Add(op(0x17));
        }
        code.Add(op(0x07)); code.Add(op(0));
        var action = new LmbAction(0, [], 0) { Code = new(0, code.Count, [.. code.Select((instruction, i) => instruction with { Offset = i })]) };
        var sprite = new LmbSpriteDefinition(1, 1, 1, 1, 0, [], [new LmbFrameLabelCommand(0, 0, 0, [], null!),
            new LmbShowFrameCommand(0, 1, [], null!), new LmbDoActionCommand(0, 0, [], null!)], null!);
        var movie = new LmbMovieDefinition(null!, null, [.. names.Select((name, index) => new LmbString(index, name, 0))],
            [], [], [], [], [], [action], [], [sprite], []);
        return new(new LumenPlayer(movie, 1280, 720, rootCharacterId: 1, hostBinding: new Recorder(calls)), LumenMatrix.Identity, 0, 0);
    }

    private sealed class Recorder(List<(string, double)> calls) : ILumenHostBinding
    {
        public void Install(LumenHostContext context) => context.RegisterFunction("Record", call =>
        {
            calls.Add((call.Arguments[0].AsString(), call.Arguments[1].Kind == LumenHostValueKind.Number
                ? call.Arguments[1].AsNumber() : double.NaN));
            return LumenHostValue.Undefined;
        });
    }

}

internal sealed class SyntheticDonPresentation : IDonPresentationController
{
    public List<DonMotionRequest> Requests { get; } = [];

    public List<DonPresentationLayout> ResetLayouts { get; } = [];

    public void Reset(DonPresentationLayout layout) => ResetLayouts.Add(layout);

    public LumenNativeSurfaceKey GetSurface(int playerIndex) => new($"don:{playerIndex}");

    public void SetMotion(DonMotionRequest request) => Requests.Add(request);
}
