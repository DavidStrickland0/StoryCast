using StoryCast.Application.Casting;
using StoryCast.Application.Characters;
using StoryCast.Application.Voices;
using StoryCast.Domain.Books;
using StoryCast.Domain.Characters;
using StoryCast.Domain.Voices;

namespace StoryCast.Application.Tests.Casting;

/// <summary>
/// Tests persistent casting continuity as a book gains characters.
/// </summary>
public sealed class CastingWorkflowTests
{
    /// <summary>
    /// Verifies that an existing narrator and character retain their voices
    /// while a newly discovered character receives an unused voice.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ExtendsExistingPlanWithoutRecasting()
    {
        var registry = CreateRegistry(
            "existing-character",
            "new-character");

        var voices = CreateVoices(
            "existing-voice",
            "narrator-voice",
            "new-voice");

        var existingAssignments =
            new[]
            {
                CreateAssignment(
                    "existing-character",
                    "existing-voice"),
                CreateAssignment(
                    "narrator",
                    "narrator-voice")
            };

        var planStore = new MemoryCastingPlanStore
        {
            Plan = new CastingPlan
            {
                SchemaVersion = 1,
                BookId = "book-1",
                Assignments = existingAssignments
            }
        };

        var castingService = new RecordingCastingService(
            [
                CreateAssignment(
                    "new-character",
                    "new-voice")
            ]);

        var workflow = new CastingWorkflow(
            new MemoryCharacterRegistryStore(registry),
            new MemoryVerifiedVoiceLibrary(voices),
            castingService,
            new CastingAssignmentValidator(),
            planStore);

        var result = await workflow.ExecuteAsync(
            CreateBook(),
            @"C:\Voices");

        Assert.Equal(1, castingService.CallCount);
        Assert.Equal(
            existingAssignments,
            castingService.ExistingAssignments);

        Assert.NotNull(planStore.Plan);
        Assert.Equal(3, planStore.Plan.Assignments.Count);
        Assert.Equal(1, planStore.SaveCount);

        AssertAssignment(
            planStore.Plan,
            "existing-character",
            "existing-voice");

        AssertAssignment(
            planStore.Plan,
            "narrator",
            "narrator-voice");

        AssertAssignment(
            planStore.Plan,
            "new-character",
            "new-voice");

        Assert.Equal(3, result.AssignmentCount);
        Assert.True(result.ReusedExistingPlan);
    }

    /// <summary>
    /// Verifies that a complete valid plan is reused without invoking
    /// automatic casting or rewriting the plan.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_CompletePlanSkipsAutomaticCasting()
    {
        var registry = CreateRegistry(
            "existing-character");

        var voices = CreateVoices(
            "existing-voice",
            "narrator-voice");

        var planStore = new MemoryCastingPlanStore
        {
            Plan = new CastingPlan
            {
                SchemaVersion = 1,
                BookId = "book-1",
                Assignments =
                [
                    CreateAssignment(
                        "existing-character",
                        "existing-voice"),
                    CreateAssignment(
                        "narrator",
                        "narrator-voice")
                ]
            }
        };

        var castingService = new RecordingCastingService([]);

        var workflow = new CastingWorkflow(
            new MemoryCharacterRegistryStore(registry),
            new MemoryVerifiedVoiceLibrary(voices),
            castingService,
            new CastingAssignmentValidator(),
            planStore);

        var result = await workflow.ExecuteAsync(
            CreateBook(),
            @"C:\Voices");

        Assert.Equal(0, castingService.CallCount);
        Assert.Equal(0, planStore.SaveCount);
        Assert.Equal(2, result.AssignmentCount);
        Assert.True(result.ReusedExistingPlan);
    }

    private static void AssertAssignment(
        CastingPlan plan,
        string characterId,
        string voiceId)
    {
        var assignment = Assert.Single(
            plan.Assignments,
            candidate => string.Equals(
                candidate.CharacterId,
                characterId,
                StringComparison.OrdinalIgnoreCase));

        Assert.Equal(voiceId, assignment.VoiceId);
    }

    private static BookProject CreateBook()
    {
        return new BookProject
        {
            SchemaVersion = 1,
            Id = "book-1",
            Title = "Test Book",
            Author = "Test Author",
            Language = "en",
            RootPath = @"C:\Book",
            Manuscript = null!
        };
    }

    private static CharacterRegistry CreateRegistry(
        params string[] characterIds)
    {
        return new CharacterRegistry
        {
            SchemaVersion = 1,
            BookId = "book-1",
            Characters = characterIds
                .Select(
                    characterId => new CharacterProfile
                    {
                        Id = characterId,
                        DisplayName = characterId
                    })
                .ToArray(),
            ProcessedChapterHashes =
                new Dictionary<string, string>()
        };
    }

    private static IReadOnlyList<VoiceProfile> CreateVoices(
        params string[] voiceIds)
    {
        return voiceIds
            .Select(
                voiceId => new VoiceProfile
                {
                    Id = voiceId,
                    SamplePath = $"{voiceId}.wav"
                })
            .ToArray();
    }

    private static CastingAssignment CreateAssignment(
        string characterId,
        string voiceId)
    {
        return new CastingAssignment
        {
            CharacterId = characterId,
            VoiceId = voiceId,
            Confidence = 0.9m,
            Rationale = "Test assignment."
        };
    }

    private sealed class MemoryCharacterRegistryStore
        : ICharacterRegistryStore
    {
        private readonly CharacterRegistry registry;

        public MemoryCharacterRegistryStore(
            CharacterRegistry registry)
        {
            this.registry = registry;
        }

        public Task<CharacterRegistry?> LoadAsync(
            BookProject book,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<CharacterRegistry?>(
                registry);
        }

        public Task SaveAsync(
            BookProject book,
            CharacterRegistry registry,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class MemoryVerifiedVoiceLibrary
        : IVerifiedVoiceLibrary
    {
        private readonly IReadOnlyList<VoiceProfile> voices;

        public MemoryVerifiedVoiceLibrary(
            IReadOnlyList<VoiceProfile> voices)
        {
            this.voices = voices;
        }

        public Task<IReadOnlyList<VoiceProfile>> LoadAsync(
            string libraryPath,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(voices);
        }
    }

    private sealed class RecordingCastingService
        : ICastingService
    {
        private readonly IReadOnlyList<CastingAssignment>
            assignments;

        public RecordingCastingService(
            IReadOnlyList<CastingAssignment> assignments)
        {
            this.assignments = assignments;
        }

        public int CallCount { get; private set; }

        public IReadOnlyList<CastingAssignment>
            ExistingAssignments
        { get; private set; } = [];

        public Task<IReadOnlyList<CastingAssignment>> AssignAsync(
            CharacterRegistry registry,
            IReadOnlyList<VoiceProfile> voices,
            IReadOnlyList<CastingAssignment> existingAssignments,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            ExistingAssignments = existingAssignments;

            return Task.FromResult(assignments);
        }
    }

    private sealed class MemoryCastingPlanStore
        : ICastingPlanStore
    {
        public CastingPlan? Plan { get; set; }

        public int SaveCount { get; private set; }

        public Task<CastingPlan?> LoadAsync(
            BookProject book,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Plan);
        }

        public Task SaveAsync(
            BookProject book,
            CastingPlan plan,
            CancellationToken cancellationToken = default)
        {
            Plan = plan;
            SaveCount++;

            return Task.CompletedTask;
        }
    }
}