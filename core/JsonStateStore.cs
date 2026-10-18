using System.Text.Json;

namespace Cortado.Core;

public sealed class JsonStateStore(String path) : IStateStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        RespectRequiredConstructorParameters = true
    };

    public SavedState Load()
    {
        if (!File.Exists(path))
        {
            return SavedState.Empty;
        }

        SavedState state;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("SchemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) ||
                !root.TryGetProperty("PreferredMode", out var preferred) || preferred.ValueKind != JsonValueKind.Number || !preferred.TryGetInt32(out var mode))
            {
                throw new InvalidDataException("Cortado state.json has invalid preferences.");
            }

            state = version == 3
                ? root.Deserialize<SavedState>(Options) ?? throw new InvalidDataException("Missing Cortado state.")
                : new(version, (KeepAwakeMode)mode);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Cortado state.json has invalid JSON.", exception);
        }
        state.Validate();
        return state;
    }

    public void Save(SavedState state)
    {
        state.Validate();
        state = state with { SchemaVersion = 3 };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, state, Options);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}