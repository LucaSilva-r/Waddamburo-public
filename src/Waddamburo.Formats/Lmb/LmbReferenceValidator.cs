using System.Collections.Immutable;
using Waddamburo.Formats.Diagnostics;

namespace Waddamburo.Formats.Lmb;

internal static class LmbReferenceValidator
{
    public static void Validate(
        LmbMovieDefinition movie,
        LmbSemanticValidationContext? context,
        ImmutableArray<ParseDiagnostic>.Builder diagnostics)
    {
        var characterIds = new HashSet<uint>();
        foreach (var shape in movie.Shapes)
        {
            addCharacterId(characterIds, shape.CharacterId, shape.RawRecord, diagnostics);
            if (shape.DeclaredGeometryCount != shape.Geometry.Length)
            {
                add(
                    diagnostics,
                    DiagnosticSeverity.Warning,
                    "LMB_GEOMETRY_COUNT_MISMATCH",
                    shape.RawRecord,
                    EvidenceStatus.Candidate,
                    $"Shape {shape.CharacterId} declares {shape.DeclaredGeometryCount} geometry records but owns {shape.Geometry.Length}.");
            }

            if (context?.TextureCount is int textureCount)
            {
                foreach (var geometry in shape.Geometry)
                {
                    validateIndex(
                        geometry.TextureIndex,
                        textureCount,
                        "LMB_TEXTURE_INDEX_OUT_OF_RANGE",
                        "texture",
                        geometry.RawRecord,
                        geometry.Evidence,
                        diagnostics);
                }
            }
        }

        foreach (var sprite in movie.Sprites)
            addCharacterId(characterIds, sprite.CharacterId, sprite.RawRecord, diagnostics);

        validateProperties(movie, diagnostics);
        validateActionStringReferences(movie, diagnostics);

        foreach (var sprite in movie.Sprites)
        {
            validateSpriteCounts(sprite, diagnostics);
            validateTimelineGroups(sprite, diagnostics);
            foreach (var command in sprite.Timeline)
            {
                switch (command)
                {
                    case LmbFrameLabelCommand label:
                        validateIndex(label.StringIndex, movie.Strings.Length, "LMB_STRING_INDEX_OUT_OF_RANGE", "string", label.Record, label.Evidence, diagnostics);
                        validateFrame(label.Frame, sprite, label.Record, label.Evidence, diagnostics);
                        break;
                    case LmbShowFrameCommand showFrame:
                        validateFrame(showFrame.Frame, sprite, showFrame.Record, showFrame.Evidence, diagnostics);
                        break;
                    case LmbFrameKeyCommand frameKey:
                        validateFrame(frameKey.Frame, sprite, frameKey.Record, frameKey.Evidence, diagnostics);
                        break;
                    case LmbPlaceObjectCommand place:
                        validatePlacement(place, sprite, movie, characterIds, diagnostics);
                        break;
                    case LmbDoActionCommand action:
                        validateIndex(action.ActionIndex, movie.Actions.Length, "LMB_ACTION_INDEX_OUT_OF_RANGE", "action", action.Record, action.Evidence, diagnostics);
                        break;
                }
            }
        }
    }

    private static void validateActionStringReferences(
        LmbMovieDefinition movie,
        ImmutableArray<ParseDiagnostic>.Builder diagnostics)
    {
        foreach (var action in movie.Actions)
            validateActionBlock(action, action.Code, movie.Strings.Length, diagnostics);
    }

    private static void validateActionBlock(
        LmbAction action,
        Avm1CodeBlock block,
        int stringCount,
        ImmutableArray<ParseDiagnostic>.Builder diagnostics)
    {
        foreach (var instruction in block.Instructions)
        {
            switch (instruction.Operand)
            {
                case Avm1StringIndexOperand reference:
                    validateActionStringIndex(action, instruction, reference.StringIndex, stringCount, diagnostics);
                    break;
                case Avm1FunctionOperand function:
                    validateActionStringIndex(action, instruction, function.NameStringIndex, stringCount, diagnostics);
                    foreach (var parameter in function.Parameters)
                        validateActionStringIndex(action, instruction, parameter.NameStringIndex, stringCount, diagnostics);
                    break;
                case Avm1PushOperand push:
                    foreach (var value in push.Values.OfType<Avm1PushStringValue>())
                        validateActionStringIndex(action, instruction, value.StringIndex, stringCount, diagnostics);
                    break;
            }

            if (instruction.Body is { } body)
                validateActionBlock(action, body, stringCount, diagnostics);
        }
    }

    private static void validateActionStringIndex(
        LmbAction action,
        Avm1Instruction instruction,
        ushort index,
        int stringCount,
        ImmutableArray<ParseDiagnostic>.Builder diagnostics)
    {
        if (index < stringCount)
            return;
        diagnostics.Add(new ParseDiagnostic(
            DiagnosticSeverity.Error,
            "LMB_AVM_STRING_INDEX_OUT_OF_RANGE",
            action.Offset + instruction.Offset,
            null,
            EvidenceStatus.Confirmed,
            $"Action {action.Index} instruction 0x{instruction.Offset:X} references string index {index}, outside the {stringCount}-entry F001 pool."));
    }

    private static void validateProperties(
        LmbMovieDefinition movie,
        ImmutableArray<ParseDiagnostic>.Builder diagnostics)
    {
        if (movie.Properties is not { } properties)
            return;

        if (properties.RootCharacterId is uint rootId && !movie.Sprites.Any(sprite => sprite.CharacterId == rootId))
        {
            add(
                diagnostics,
                DiagnosticSeverity.Warning,
                "LMB_ROOT_SPRITE_NOT_FOUND",
                properties.RawRecord,
                properties.Evidence,
                $"Candidate root character {rootId} is not a defined sprite.");
        }

        if (properties.FrameRate is float frameRate && (!float.IsFinite(frameRate) || frameRate <= 0))
        {
            add(
                diagnostics,
                DiagnosticSeverity.Warning,
                "LMB_INVALID_FRAME_RATE",
                properties.RawRecord,
                properties.Evidence,
                $"Candidate frame rate {frameRate} is not finite and positive.");
        }
    }

    private static void validateSpriteCounts(
        LmbSpriteDefinition sprite,
        ImmutableArray<ParseDiagnostic>.Builder diagnostics)
    {
        var labelCount = sprite.Timeline.Count(command => command is LmbFrameLabelCommand);
        if (sprite.DeclaredLabelCount != labelCount || sprite.RepeatedLabelCount != labelCount)
        {
            add(
                diagnostics,
                DiagnosticSeverity.Warning,
                "LMB_LABEL_COUNT_MISMATCH",
                sprite.RawRecord,
                sprite.HeaderEvidence,
                $"Sprite {sprite.CharacterId} declares label counts {sprite.DeclaredLabelCount}/{sprite.RepeatedLabelCount} but owns {labelCount} labels.");
        }

        var frameCount = sprite.Timeline.Count(command => command is LmbShowFrameCommand);
        if (sprite.DeclaredFrameCount != frameCount)
        {
            add(
                diagnostics,
                DiagnosticSeverity.Warning,
                "LMB_FRAME_COUNT_MISMATCH",
                sprite.RawRecord,
                sprite.HeaderEvidence,
                $"Sprite {sprite.CharacterId} declares {sprite.DeclaredFrameCount} frames but owns {frameCount} ordinary frame records.");
        }
    }

    private static void validateTimelineGroups(
        LmbSpriteDefinition sprite,
        ImmutableArray<ParseDiagnostic>.Builder diagnostics)
    {
        LmbTimelineCommand? boundary = null;
        uint expectedCount = 0;
        uint actualCount = 0;
        foreach (var command in sprite.Timeline)
        {
            if (command is LmbShowFrameCommand showFrame)
            {
                validateGroupCount(boundary, expectedCount, actualCount, diagnostics);
                boundary = showFrame;
                expectedCount = showFrame.DeclaredCommandCount;
                actualCount = 0;
            }
            else if (command is LmbFrameKeyCommand frameKey)
            {
                validateGroupCount(boundary, expectedCount, actualCount, diagnostics);
                boundary = frameKey;
                expectedCount = frameKey.EntryCount;
                actualCount = 0;
            }
            else if (command is not LmbFrameLabelCommand)
            {
                if (boundary is null)
                {
                    add(
                        diagnostics,
                        DiagnosticSeverity.Warning,
                        "LMB_UNASSIGNED_TIMELINE_COMMAND",
                        command.RawRecord,
                        command.Evidence,
                        "Timeline command appears before an ordinary frame or key-frame boundary.");
                }
                else
                {
                    actualCount++;
                }
            }
        }
        validateGroupCount(boundary, expectedCount, actualCount, diagnostics);
    }

    private static void validateGroupCount(
        LmbTimelineCommand? boundary,
        uint expectedCount,
        uint actualCount,
        ImmutableArray<ParseDiagnostic>.Builder diagnostics)
    {
        if (boundary is null || expectedCount == actualCount)
            return;

        var (code, label) = boundary switch
        {
            LmbShowFrameCommand => ("LMB_FRAME_COMMAND_COUNT_MISMATCH", "Frame"),
            LmbFrameKeyCommand => ("LMB_KEY_ENTRY_COUNT_MISMATCH", "Key frame"),
            _ => throw new InvalidOperationException("Unsupported timeline boundary."),
        };
        add(
            diagnostics,
            DiagnosticSeverity.Warning,
            code,
            boundary.RawRecord,
            boundary.Evidence,
            $"{label} declares {expectedCount} entries but owns {actualCount} commands.");
    }

    private static void validatePlacement(
        LmbPlaceObjectCommand place,
        LmbSpriteDefinition sprite,
        LmbMovieDefinition movie,
        HashSet<uint> characterIds,
        ImmutableArray<ParseDiagnostic>.Builder diagnostics)
    {
        if (place.CharacterId is not 0 and not uint.MaxValue && !characterIds.Contains(place.CharacterId))
        {
            add(
                diagnostics,
                DiagnosticSeverity.Warning,
                "LMB_CHARACTER_ID_NOT_FOUND",
                place.Record,
                place.Evidence,
                $"Placement references undefined character {place.CharacterId}.");
        }

        validateIndex(place.NameStringIndex, movie.Strings.Length, "LMB_STRING_INDEX_OUT_OF_RANGE", "string", place.Record, place.Evidence, diagnostics);
        validateFrame(place.FirstFrame, sprite, place.Record, place.Evidence, diagnostics);

        switch (place.PositionKind)
        {
            case 0:
                validateIndex(place.PositionIndex, movie.Matrices.Length, "LMB_POSITION_INDEX_OUT_OF_RANGE", "matrix", place.Record, place.Evidence, diagnostics);
                break;
            case 0x8000:
                validateIndex(place.PositionIndex, movie.Translations.Length, "LMB_POSITION_INDEX_OUT_OF_RANGE", "translation", place.Record, place.Evidence, diagnostics);
                break;
            case ushort.MaxValue:
                break;
            default:
                add(
                    diagnostics,
                    DiagnosticSeverity.Warning,
                    "LMB_UNKNOWN_POSITION_KIND",
                    place.Record,
                    EvidenceStatus.Unknown,
                    $"Placement uses unknown position kind 0x{place.PositionKind:X4}; raw words are retained.");
                break;
        }

        validateOptionalIndex(place.ColorMultiplyIndex, movie.ColorTransforms.Length, "color transform", place.Record, place.Evidence, diagnostics);
        validateOptionalIndex(place.ColorAddIndex, movie.ColorTransforms.Length, "color transform", place.Record, place.Evidence, diagnostics);
    }

    private static void validateFrame(
        uint frame,
        LmbSpriteDefinition sprite,
        LmbRecord record,
        EvidenceStatus evidence,
        ImmutableArray<ParseDiagnostic>.Builder diagnostics)
    {
        if (frame < sprite.DeclaredFrameCount)
            return;
        add(
            diagnostics,
            DiagnosticSeverity.Error,
            "LMB_FRAME_OUT_OF_RANGE",
            record,
            evidence,
            $"Frame {frame} is outside sprite {sprite.CharacterId}'s declared {sprite.DeclaredFrameCount} frames.");
    }

    private static void validateOptionalIndex(
        uint index,
        int count,
        string target,
        LmbRecord record,
        EvidenceStatus evidence,
        ImmutableArray<ParseDiagnostic>.Builder diagnostics)
    {
        if (index == uint.MaxValue)
            return;
        validateIndex(index, count, "LMB_COLOR_INDEX_OUT_OF_RANGE", target, record, evidence, diagnostics);
    }

    private static void validateIndex(
        uint index,
        int count,
        string code,
        string target,
        LmbRecord record,
        EvidenceStatus evidence,
        ImmutableArray<ParseDiagnostic>.Builder diagnostics)
    {
        if ((ulong)index < (ulong)count)
            return;
        add(
            diagnostics,
            DiagnosticSeverity.Error,
            code,
            record,
            evidence,
            $"Index {index} does not reference one of the {count} available {target} entries.");
    }

    private static void addCharacterId(
        HashSet<uint> ids,
        uint id,
        LmbRecord record,
        ImmutableArray<ParseDiagnostic>.Builder diagnostics)
    {
        if (ids.Add(id))
            return;
        add(
            diagnostics,
            DiagnosticSeverity.Error,
            "LMB_DUPLICATE_CHARACTER_ID",
            record,
            EvidenceStatus.Candidate,
            $"Character ID {id} is defined more than once.");
    }

    private static void add(
        ImmutableArray<ParseDiagnostic>.Builder diagnostics,
        DiagnosticSeverity severity,
        string code,
        LmbRecord record,
        EvidenceStatus evidence,
        string message) =>
        diagnostics.Add(new ParseDiagnostic(
            severity,
            code,
            record.HeaderOffset,
            record.Index,
            evidence,
            message));
}
