using StoryCast.Application.Characters;
using StoryCast.Application.Voices;
using StoryCast.Domain.Books;
using StoryCast.Domain.Voices;

namespace StoryCast.Application.Casting;

/// <summary>
/// Creates or reuses a verified, persistent casting plan for a book.
/// </summary>
public sealed class CastingWorkflow
{
    private readonly ICharacterRegistryStore characterRegistryStore;
    private readonly IVerifiedVoiceLibrary verifiedVoiceLibrary;
    private readonly ICastingService castingService;
    private readonly CastingAssignmentValidator validator;
    private readonly ICastingPlanStore castingPlanStore;

    /// <summary>
    /// Initializes the casting workflow.
    /// </summary>
    /// <param name="characterRegistryStore">
    /// The persistent character-registry store.
    /// </param>
    /// <param name="verifiedVoiceLibrary">
    /// The verified voice-library loader.
    /// </param>
    /// <param name="castingService">
    /// The automatic casting service.
    /// </param>
    /// <param name="validator">
    /// The deterministic assignment validator.
    /// </param>
    /// <param name="castingPlanStore">
    /// The persistent casting-plan store.
    /// </param>
    public CastingWorkflow(
        ICharacterRegistryStore characterRegistryStore,
        IVerifiedVoiceLibrary verifiedVoiceLibrary,
        ICastingService castingService,
        CastingAssignmentValidator validator,
        ICastingPlanStore castingPlanStore)
    {
        this.characterRegistryStore =
            characterRegistryStore ??
            throw new ArgumentNullException(
                nameof(characterRegistryStore));

        this.verifiedVoiceLibrary =
            verifiedVoiceLibrary ??
            throw new ArgumentNullException(
                nameof(verifiedVoiceLibrary));

        this.castingService =
            castingService ??
            throw new ArgumentNullException(
                nameof(castingService));

        this.validator =
            validator ??
            throw new ArgumentNullException(
                nameof(validator));

        this.castingPlanStore =
            castingPlanStore ??
            throw new ArgumentNullException(
                nameof(castingPlanStore));
    }

    /// <summary>
    /// Creates or reuses the casting plan for a book.
    /// </summary>
    /// <param name="book">The configured book project.</param>
    /// <param name="voiceLibraryPath">
    /// The shared voice-library directory.
    /// </param>
    /// <param name="cancellationToken">
    /// A token that may cancel the operation.
    /// </param>
    /// <returns>The casting workflow summary.</returns>
    public async Task<CastingWorkflowResult> ExecuteAsync(
        BookProject book,
        string voiceLibraryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentException.ThrowIfNullOrWhiteSpace(
            voiceLibraryPath);

        var registry = await characterRegistryStore.LoadAsync(
            book,
            cancellationToken);

        if (registry is null)
        {
            throw new InvalidDataException(
                "Character discovery must be completed before casting " +
                $"book '{book.Id}'.");
        }

        var voices = await verifiedVoiceLibrary.LoadAsync(
            voiceLibraryPath,
            cancellationToken);

        var existingPlan = await castingPlanStore.LoadAsync(
            book,
            cancellationToken);

        var existingAssignments =
            existingPlan?.Assignments ??
            [];

        var requiredRoleIds = registry.Characters
            .Select(character => character.Id)
            .Append("narrator")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var existingRoleIds = existingAssignments
            .Select(assignment => assignment.CharacterId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missingRoleIds = requiredRoleIds
            .Except(
                existingRoleIds,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (missingRoleIds.Length == 0)
        {
            validator.Validate(
                registry,
                voices,
                existingAssignments);

            return new CastingWorkflowResult
            {
                EligibleVoiceCount = voices.Count,
                AssignmentCount = existingAssignments.Count,
                ReusedExistingPlan = true
            };
        }

        var newAssignments = await castingService.AssignAsync(
            registry,
            voices,
            existingAssignments,
            cancellationToken);

        var assignments = existingAssignments
            .Concat(newAssignments)
            .ToArray();

        validator.Validate(
            registry,
            voices,
            assignments);

        var plan = new CastingPlan
        {
            SchemaVersion = 1,
            BookId = book.Id,
            Assignments = assignments
        };

        await castingPlanStore.SaveAsync(
            book,
            plan,
            cancellationToken);

        return new CastingWorkflowResult
        {
            EligibleVoiceCount = voices.Count,
            AssignmentCount = assignments.Length,
            ReusedExistingPlan =
                existingAssignments.Count > 0
        };
    }
}
