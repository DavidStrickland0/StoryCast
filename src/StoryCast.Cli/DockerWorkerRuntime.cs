using System.ComponentModel;
using System.Diagnostics;
using System.Text;

internal sealed class DockerWorkerRuntime
{
    private const string ContainerBookPath = "/books";
    private const string ContainerLibraryPath = "/voices";
    private const string ContainerModelsPath = "/models";
    private const string ContainerToolsPath = "/app/tools";
    private const string DefaultModelsVolume = "storycast-models";

    private readonly string? bookPath;
    private readonly string libraryPath;
    private readonly bool libraryReadOnly;
    private readonly string image;
    private readonly string modelsVolume;

    /// <summary>
    /// Initializes a Docker-backed Python worker runtime.
    /// </summary>
    /// <param name="bookPath">
    /// The optional host book directory mounted read-write at
    /// <c>/books</c>.
    /// </param>
    /// <param name="libraryPath">
    /// The host voice-library directory mounted read-only at
    /// <c>/voices</c>.
    /// </param>
    /// <param name="image">The Docker worker image name.</param>
    /// <param name="libraryReadOnly">
    /// Whether the voice-library mount is read-only.
    /// </param>
    /// <param name="modelsVolume">
    /// The persistent Docker volume used for model caches.
    /// </param>
    public DockerWorkerRuntime(
        string? bookPath,
        string libraryPath,
        string image = "storycast-worker:dev",
        bool libraryReadOnly = true,
        string modelsVolume = DefaultModelsVolume)
    {
        this.bookPath = bookPath is null
            ? null
            : Path.GetFullPath(bookPath);

        this.libraryPath = Path.GetFullPath(
            libraryPath ??
            throw new ArgumentNullException(nameof(libraryPath)));

        this.libraryReadOnly = libraryReadOnly;

        this.image = string.IsNullOrWhiteSpace(image)
            ? throw new ArgumentException(
                "The Docker worker image is required.",
                nameof(image))
            : image;

        this.modelsVolume =
            string.IsNullOrWhiteSpace(modelsVolume)
                ? throw new ArgumentException(
                    "The Docker models volume is required.",
                    nameof(modelsVolume))
                : modelsVolume;
    }

    /// <summary>
    /// Gets the voice-library path visible inside the container.
    /// </summary>
    /// <summary>
    /// Gets standard-error output captured from the most recent worker.
    /// </summary>
    public string LastErrorOutput { get; private set; } =
        string.Empty;
    public string LibraryPath => ContainerLibraryPath;

    /// <summary>
    /// Converts the host book root to its container path.
    /// </summary>
    /// <returns>The container book-root path.</returns>
    public string GetBookPath()
    {
        EnsureBookPath();

        return ContainerBookPath;
    }

    /// <summary>
    /// Converts a host path beneath the configured book root into its
    /// container path.
    /// </summary>
    /// <param name="hostPath">The host path to convert.</param>
    /// <returns>The corresponding path beneath <c>/books</c>.</returns>
    public string GetBookPath(string hostPath)
    {
        EnsureBookPath();

        var fullPath = Path.GetFullPath(hostPath);

        var relativePath = Path.GetRelativePath(
            bookPath!,
            fullPath);

        if (Path.IsPathRooted(relativePath) ||
            string.Equals(
                relativePath,
                "..",
                StringComparison.Ordinal) ||
            relativePath.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal) ||
            relativePath.StartsWith(
                $"..{Path.AltDirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Path is outside the configured book directory: " +
                $"{fullPath}");
        }

        if (string.Equals(
                relativePath,
                ".",
                StringComparison.Ordinal))
        {
            return ContainerBookPath;
        }

        return $"{ContainerBookPath}/" +
            relativePath.Replace('\\', '/');
    }

    /// <summary>
    /// Executes one packaged Python worker in Docker.
    /// </summary>
    /// <param name="scriptName">
    /// The filename of the script beneath <c>/app/tools</c>.
    /// </param>
    /// <param name="arguments">Arguments passed to the worker.</param>
    /// <param name="useGpu">
    /// Whether the container receives all available GPUs.
    /// </param>
    /// <returns>The Docker process exit code.</returns>
    public async Task<int> RunAsync(
        string scriptName,
        IEnumerable<string> arguments,
        bool useGpu = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptName);
        ArgumentNullException.ThrowIfNull(arguments);

        if (Path.GetFileName(scriptName) != scriptName)
        {
            throw new ArgumentException(
                "The worker script must be a filename.",
                nameof(scriptName));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "docker",
            UseShellExecute = false,
            RedirectStandardError = true
        };

        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--rm");

        var cpuOnly = string.Equals(
            Environment.GetEnvironmentVariable("STORYCAST_DEVICE"),
            "cpu",
            StringComparison.OrdinalIgnoreCase);

        if (cpuOnly)
        {
            startInfo.ArgumentList.Add("--env");
            startInfo.ArgumentList.Add("STORYCAST_DEVICE=cpu");
        }

        if (useGpu && !cpuOnly)
        {
            startInfo.ArgumentList.Add("--gpus");
            startInfo.ArgumentList.Add("all");
        }

        if (bookPath is not null)
        {
            AddMount(
                startInfo,
                "bind",
                bookPath,
                ContainerBookPath);
        }

        AddMount(
            startInfo,
            "bind",
            libraryPath,
            ContainerLibraryPath,
            readOnly: libraryReadOnly);

        AddMount(
            startInfo,
            "volume",
            modelsVolume,
            ContainerModelsPath);

        startInfo.ArgumentList.Add(image);
        startInfo.ArgumentList.Add(
            $"{ContainerToolsPath}/{scriptName}");

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var errorOutput = new StringBuilder();
        using var process = new Process
        {
            StartInfo = startInfo
        };

        process.ErrorDataReceived +=
            (_, eventArgs) =>
            {
                if (eventArgs.Data is null)
                {
                    return;
                }

                errorOutput.AppendLine(eventArgs.Data);
                Console.Error.WriteLine(eventArgs.Data);
            };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException(
                    "Unable to start the Docker worker.");
            }
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(
                "Unable to start Docker. Ensure Docker Desktop is " +
                "installed and running.",
                exception);
        }

        process.BeginErrorReadLine();

        await process.WaitForExitAsync();
        process.WaitForExit();

        LastErrorOutput = errorOutput.ToString();

        return process.ExitCode;
    }

    private static void AddMount(
        ProcessStartInfo startInfo,
        string type,
        string source,
        string target,
        bool readOnly = false)
    {
        startInfo.ArgumentList.Add("--mount");

        var mount =
            $"type={type},source={source},target={target}";

        if (readOnly)
        {
            mount += ",readonly";
        }

        startInfo.ArgumentList.Add(mount);
    }

    private void EnsureBookPath()
    {
        if (bookPath is null)
        {
            throw new InvalidOperationException(
                "This Docker worker has no configured book directory.");
        }
    }
}
