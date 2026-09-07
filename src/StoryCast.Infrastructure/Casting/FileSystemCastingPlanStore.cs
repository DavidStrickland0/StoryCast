using System.Text.Json;
using StoryCast.Application.Casting;
using StoryCast.Domain.Books;
using StoryCast.Domain.Voices;

namespace StoryCast.Infrastructure.Casting;

/// <summary>
/// Stores per-book casting plans as atomic JSON files.
/// </summary>
public sealed class FileSystemCastingPlanStore
    : ICastingPlanStore
{
    private const int SupportedSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions =
        new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

    /// <inheritdoc />
    public async Task<CastingPlan?> LoadAsync(
        BookProject book,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(book);

        var planPath = GetPlanPath(book);

        if (!File.Exists(planPath))
        {
            return null;
        }

        await using var stream = new FileStream(
            planPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        CastingPlan plan;

        try
        {
            plan =
                await JsonSerializer.DeserializeAsync<CastingPlan>(
                    stream,
                    SerializerOptions,
                    cancellationToken) ??
                throw new InvalidDataException(
                    $"Casting plan is empty: {planPath}");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Casting plan contains invalid JSON: {planPath}",
                exception);
        }

        ValidatePlan(
            book,
            plan,
            planPath);

        return plan;
    }

    /// <inheritdoc />
    public async Task SaveAsync(
        BookProject book,
        CastingPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(plan);

        var planPath = GetPlanPath(book);

        ValidatePlan(
            book,
            plan,
            planPath);

        var productionDirectory =
            Path.GetDirectoryName(planPath) ??
            throw new InvalidDataException(
                $"Casting plan has no parent directory: {planPath}");

        Directory.CreateDirectory(productionDirectory);

        var temporaryPath = Path.Combine(
            productionDirectory,
            $".casting-{Guid.NewGuid():N}.tmp");

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
                    plan,
                    SerializerOptions,
                    cancellationToken);

                await stream.WriteAsync(
                    "\n"u8.ToArray(),
                    cancellationToken);

                await stream.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(planPath))
            {
                File.Replace(
                    temporaryPath,
                    planPath,
                    destinationBackupFileName: null);
            }
            else
            {
                File.Move(
                    temporaryPath,
                    planPath);
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

    private static string GetPlanPath(BookProject book)
    {
        return Path.Combine(
            book.RootPath,
            "production",
            "casting.json");
    }

    private static void ValidatePlan(
        BookProject book,
        CastingPlan plan,
        string planPath)
    {
        if (plan.SchemaVersion != SupportedSchemaVersion)
        {
            throw new InvalidDataException(
                $"Casting-plan schema version {plan.SchemaVersion} is " +
                $"unsupported: {planPath}");
        }

        if (!string.Equals(
                plan.BookId,
                book.Id,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Casting plan belongs to book '{plan.BookId}', not " +
                $"'{book.Id}': {planPath}");
        }

        var roleIds = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        var voiceIds = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var assignment in plan.Assignments)
        {
            if (string.IsNullOrWhiteSpace(
                    assignment.CharacterId))
            {
                throw new InvalidDataException(
                    $"Casting plan contains an empty role ID: " +
                    planPath);
            }

            if (string.IsNullOrWhiteSpace(
                    assignment.VoiceId))
            {
                throw new InvalidDataException(
                    $"Casting plan contains an empty voice ID: " +
                    planPath);
            }

            if (!roleIds.Add(assignment.CharacterId))
            {
                throw new InvalidDataException(
                    $"Casting plan contains duplicate role " +
                    $"'{assignment.CharacterId}': {planPath}");
            }

            if (!voiceIds.Add(assignment.VoiceId))
            {
                throw new InvalidDataException(
                    $"Casting plan assigns voice " +
                    $"'{assignment.VoiceId}' more than once: " +
                    planPath);
            }

            if (assignment.Confidence < 0 ||
                assignment.Confidence > 1)
            {
                throw new InvalidDataException(
                    $"Casting assignment '{assignment.CharacterId}' " +
                    $"has invalid confidence " +
                    $"{assignment.Confidence}: {planPath}");
            }
        }
    }
}
