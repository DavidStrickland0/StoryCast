using System.Text.Json;
using System.Text.Json.Serialization;
using StoryCast.Application.Characters;
using StoryCast.Domain.Books;
using StoryCast.Domain.Characters;

namespace StoryCast.Infrastructure.Characters;

/// <summary>
/// Stores character registries as atomic JSON files.
/// </summary>
public sealed class FileSystemCharacterRegistryStore
    : ICharacterRegistryStore
{
    private const string ProductionDirectoryName = "production";
    private const string RegistryFileName = "characters.json";
    private const int SupportedSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions =
        new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            Converters =
            {
                new JsonStringEnumConverter(
                    JsonNamingPolicy.CamelCase)
            }
        };

    /// <inheritdoc />
    public async Task<CharacterRegistry?> LoadAsync(
        BookProject book,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(book);

        var registryPath = GetRegistryPath(book);

        if (!File.Exists(registryPath))
        {
            return null;
        }

        await using var stream = new FileStream(
            registryPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        CharacterRegistry registry;

        try
        {
            registry =
                await JsonSerializer.DeserializeAsync<CharacterRegistry>(
                    stream,
                    SerializerOptions,
                    cancellationToken) ??
                throw new InvalidDataException(
                    $"Character registry is empty: {registryPath}");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Character registry contains invalid JSON: {registryPath}",
                exception);
        }

        ValidateRegistry(
            book,
            registry,
            registryPath);

        return registry;
    }

    /// <inheritdoc />
    public async Task SaveAsync(
        BookProject book,
        CharacterRegistry registry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(registry);

        var registryPath = GetRegistryPath(book);

        ValidateRegistry(
            book,
            registry,
            registryPath);

        var productionDirectory =
            Path.GetDirectoryName(registryPath) ??
            throw new InvalidDataException(
                $"Registry has no parent directory: {registryPath}");

        Directory.CreateDirectory(productionDirectory);

        var temporaryPath = Path.Combine(
            productionDirectory,
            $".characters-{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    registry,
                    SerializerOptions,
                    cancellationToken);

                await stream.WriteAsync(
                    "\n"u8.ToArray(),
                    cancellationToken);

                await stream.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(registryPath))
            {
                File.Replace(
                    temporaryPath,
                    registryPath,
                    destinationBackupFileName: null);
            }
            else
            {
                File.Move(
                    temporaryPath,
                    registryPath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string GetRegistryPath(BookProject book)
    {
        return Path.Combine(
            book.RootPath,
            ProductionDirectoryName,
            RegistryFileName);
    }

    private static void ValidateRegistry(
        BookProject book,
        CharacterRegistry registry,
        string registryPath)
    {
        if (registry.SchemaVersion != SupportedSchemaVersion)
        {
            throw new InvalidDataException(
                $"Character registry schema version " +
                $"{registry.SchemaVersion} is unsupported: {registryPath}");
        }

        if (!string.Equals(
                book.Id,
                registry.BookId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Character registry belongs to book " +
                $"'{registry.BookId}', not '{book.Id}': {registryPath}");
        }

        var characterIds = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var character in registry.Characters)
        {
            if (string.IsNullOrWhiteSpace(character.Id))
            {
                throw new InvalidDataException(
                    $"Character registry contains an empty ID: " +
                    registryPath);
            }

            if (!characterIds.Add(character.Id))
            {
                throw new InvalidDataException(
                    $"Character registry contains duplicate ID " +
                    $"'{character.Id}': {registryPath}");
            }
        }

        foreach (var pair in registry.ProcessedChapterHashes)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) ||
                string.IsNullOrWhiteSpace(pair.Value))
            {
                throw new InvalidDataException(
                    $"Character registry contains an invalid chapter hash: " +
                    registryPath);
            }
        }
    }
}
