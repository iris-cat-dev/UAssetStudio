using System.Text.Json;
using System.Text.Json.Serialization;

namespace UAssetStudio.Cli.CMD
{
    /// <summary>
    /// Structured error payload for machine-readable (--json) output.
    /// </summary>
    public sealed class CliError
    {
        public string Type { get; set; } = "";
        public string Message { get; set; } = "";
        public string? Hint { get; set; }
    }

    /// <summary>
    /// Single structured result object emitted by every CLI command.
    /// In --json mode this is serialized as the only thing printed to stdout,
    /// so an agent can parse it deterministically.
    /// </summary>
    public sealed class CommandResult
    {
        /// <summary>Command name, e.g. "decompile".</summary>
        public string Command { get; set; }

        /// <summary>"ok" (exit 0) | "error" (exit 1) | "failed" (exit 2, judgment did not pass).</summary>
        public string Status { get; set; } = "ok";

        /// <summary>Echo of the resolved inputs (paths, flags) for traceability.</summary>
        public object? Inputs { get; set; }

        /// <summary>Absolute/relative paths of files produced by the command.</summary>
        public List<string> Outputs { get; } = new();

        /// <summary>Non-fatal warnings.</summary>
        public List<string> Warnings { get; } = new();

        /// <summary>Populated when Status is "error" or "failed".</summary>
        public CliError? Error { get; set; }

        /// <summary>Command-specific structured payload (counts, diffs, summaries...).</summary>
        public object? Data { get; set; }

        /// <summary>Human-readable lines, only printed in text mode. Never serialized.</summary>
        [JsonIgnore]
        public List<string> HumanLines { get; } = new();

        public CommandResult(string command) => Command = command;

        public void AddOutput(string path) => Outputs.Add(path);

        public void Warn(string message) => Warnings.Add(message);

        /// <summary>Add a human-readable line (text mode only).</summary>
        public void Line(string line) => HumanLines.Add(line);

        /// <summary>Mark this result as a judgment failure (exit code 2).</summary>
        public void Fail(string type, string message, string? hint = null)
        {
            Status = "failed";
            Error = new CliError { Type = type, Message = message, Hint = hint };
        }
    }

    /// <summary>
    /// Exception that carries a structured error type/hint and maps to exit code 1.
    /// Throw from a command body to abort with a clean machine-readable error.
    /// </summary>
    public sealed class CliException : Exception
    {
        public string ErrorType { get; }
        public string? Hint { get; }

        public CliException(string errorType, string message, string? hint = null) : base(message)
        {
            ErrorType = errorType;
            Hint = hint;
        }
    }

    /// <summary>
    /// Wraps command handlers to produce a uniform structured result, consistent
    /// exit codes, and a text/JSON dual output mode.
    /// Exit codes: 0 = ok, 1 = error, 2 = judgment failure (validate/verify mismatch).
    /// </summary>
    public static class CliOutput
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>
        /// Runs a command body, capturing exceptions into a structured result.
        /// Returns the process exit code.
        /// </summary>
        public static int Run(string command, bool json, object? inputs, Action<CommandResult> body)
        {
            var result = new CommandResult(command) { Inputs = inputs };

            // In JSON mode, suppress any stray stdout writes from deep libraries (e.g. the linker)
            // so the only thing printed to stdout is the single structured JSON object.
            var originalOut = Console.Out;
            if (json)
                Console.SetOut(TextWriter.Null);

            try
            {
                body(result);
            }
            catch (CliException ex)
            {
                result.Status = "error";
                result.Error = new CliError { Type = ex.ErrorType, Message = ex.Message, Hint = ex.Hint };
            }
            catch (Exception ex)
            {
                result.Status = "error";
                result.Error = new CliError { Type = ex.GetType().Name, Message = ex.Message };
            }
            finally
            {
                if (json)
                    Console.SetOut(originalOut);
            }

            Emit(result, json);
            return result.Status switch
            {
                "ok" => 0,
                "failed" => 2,
                _ => 1,
            };
        }

        /// <summary>Emits the result as JSON (machine mode) or human-readable text.</summary>
        public static void Emit(CommandResult result, bool json)
        {
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                return;
            }

            foreach (var line in result.HumanLines)
                Console.WriteLine(line);

            foreach (var output in result.Outputs)
                Console.WriteLine($"Output: {output}");

            foreach (var warning in result.Warnings)
                Console.WriteLine($"[Warn] {warning}");

            if (result.Error != null)
            {
                Console.Error.WriteLine($"[{result.Status}] {result.Error.Type}: {result.Error.Message}");
                if (!string.IsNullOrEmpty(result.Error.Hint))
                    Console.Error.WriteLine($"Hint: {result.Error.Hint}");
            }
        }

        /// <summary>Throws a structured "file not found" error (exit 1).</summary>
        public static void RequireFile(string path, string role)
        {
            if (!File.Exists(path))
                throw new CliException("FileNotFound", $"{role} not found: {path}");
        }
    }
}
