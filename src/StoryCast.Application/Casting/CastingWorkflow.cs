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

        if (existingPlan is not null)
        {
            try
            {
                validator.Validate(
                    registry,
                    voices,
                    existingPlan.Assignments);

                return new CastingWorkflowResult
                {
                    EligibleVoiceCount = voices.Count,
                    AssignmentCount =
                        existingPlan.Assignments.Count,
                    ReusedExistingPlan = true
                };
            }
            catch (InvalidDataException exception)
            {
                if (existingPlan.Assignments.Any(
                        assignment => assignment.IsLocked))
                {
                    throw new InvalidDataException(
                        "The existing casting plan is stale and contains " +
                        "locked assignments. Resolve or unlock those " +
                        "assignments before recasting.",
                        exception);
                }
            }
        }

        var assignments = await castingService.AssignAsync(
            registry,
            voices,
            cancellationToken);

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
            AssignmentCount = assignments.Count,
            ReusedExistingPlan = false
        };
    }
}
