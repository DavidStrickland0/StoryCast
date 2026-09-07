using StoryCast.Domain.Characters;
using StoryCast.Domain.Voices;

namespace StoryCast.Application.Casting;

/// <summary>
/// Validates complete, unique audiobook casting assignments.
/// </summary>
public sealed class CastingAssignmentValidator
{
    /// <summary>
    /// Validates generated assignments against required roles and voices.
    /// </summary>
    /// <param name="registry">The established character registry.</param>
    /// <param name="voices">The eligible voice profiles.</param>
    /// <param name="assignments">The assignments to validate.</param>
    public void Validate(
        CharacterRegistry registry,
        IReadOnlyList<VoiceProfile> voices,
        IReadOnlyList<CastingAssignment> assignments)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(voices);
        ArgumentNullException.ThrowIfNull(assignments);

        var requiredRoleIds = registry.Characters
            .Select(character => character.Id)
            .Append("narrator")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var charactersById = registry.Characters.ToDictionary(
            character => character.Id,
            StringComparer.OrdinalIgnoreCase);

        var voicesById = voices.ToDictionary(
            voice => voice.Id,
            StringComparer.OrdinalIgnoreCase);

        if (voicesById.Count < requiredRoleIds.Count)
        {
            throw new InvalidDataException(
                $"Casting requires {requiredRoleIds.Count} unique voices " +
                $"but only {voicesById.Count} are eligible.");
        }

        var assignedRoleIds = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        var assignedVoiceIds = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var assignment in assignments)
        {
            if (!requiredRoleIds.Contains(
                    assignment.CharacterId))
            {
                throw new InvalidDataException(
                    $"Casting contains unknown role " +
                    $"'{assignment.CharacterId}'.");
            }

            if (!assignedRoleIds.Add(
                    assignment.CharacterId))
            {
                throw new InvalidDataException(
                    $"Role '{assignment.CharacterId}' was assigned " +
                    "more than once.");
            }

            if (!voicesById.TryGetValue(
                    assignment.VoiceId,
                    out var assignedVoice))
            {
                throw new InvalidDataException(
                    $"Casting uses ineligible voice " +
                    $"'{assignment.VoiceId}'.");
            }

            if (!assignedVoiceIds.Add(
                    assignment.VoiceId))
            {
                throw new InvalidDataException(
                    $"Voice '{assignment.VoiceId}' was assigned " +
                    "more than once.");
            }

            if (charactersById.TryGetValue(
                    assignment.CharacterId,
                    out var character))
            {
                ValidatePresentation(
                    character,
                    assignedVoice);
            }

            if (assignment.Confidence < 0 ||
                assignment.Confidence > 1)
            {
                throw new InvalidDataException(
                    $"Role '{assignment.CharacterId}' has invalid " +
                    $"casting confidence {assignment.Confidence}.");
            }
        }

        var missingRoleIds = requiredRoleIds
            .Except(
                assignedRoleIds,
                StringComparer.OrdinalIgnoreCase)
            .OrderBy(
                roleId => roleId,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (missingRoleIds.Length > 0)
        {
            throw new InvalidDataException(
                $"Casting is missing required roles: " +
                $"{string.Join(", ", missingRoleIds)}.");
        }

        if (assignments.Count != requiredRoleIds.Count)
        {
            throw new InvalidDataException(
                $"Expected {requiredRoleIds.Count} casting assignments " +
                $"but received {assignments.Count}.");
        }
    }

    private static void ValidatePresentation(
        CharacterProfile character,
        VoiceProfile voice)
    {
        var requiredPresentation =
            character.VoicePresentation.Trim();

        if (string.IsNullOrWhiteSpace(requiredPresentation) ||
            string.Equals(
                requiredPresentation,
                "unspecified",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!string.Equals(
                requiredPresentation,
                "male",
                StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(
                requiredPresentation,
                "female",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Character '{character.Id}' has unsupported voice " +
                $"presentation '{character.VoicePresentation}'.");
        }

        if (!string.Equals(
                requiredPresentation,
                voice.Presentation,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Character '{character.Id}' requires a " +
                $"'{requiredPresentation}' voice but was assigned " +
                $"'{voice.Id}' with presentation " +
                $"'{voice.Presentation}'.");
        }
    }
}
