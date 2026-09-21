using NetAI.ResxTranslator.Core;
using NetAI.ResxTranslator.Core.Config;
using Microsoft.Build.Framework;

using Task = Microsoft.Build.Utilities.Task;

namespace NetAI.ResxTranslator.Tasks
{
    public class ResxAiTranslatorTask : Task
    {
        [Required]
        public string ProjectDir { get; set; } = string.Empty;

        // Nur noch das, was sich aus dem Build-Kontext ergibt,
        // bleibt als MSBuild-Property. Alles andere kommt aus aisettings.json.
        public string? CurrentConfiguration { get; set; }
        public bool IsPublishing { get; set; }

        public override bool Execute()
        {
            // WPF protection: if project name ends with "_wpftmp", abort silently
            if (!string.IsNullOrEmpty(ProjectDir) && ProjectDir.EndsWith("_wpftmp", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            AiTestingConfig config;
            try
            {
                config = AiSettingsLoader.Load(ProjectDir);
            }
            catch (Exception ex)
            {
                Log.LogError($"[AI-Translator] aisettings.json konnte nicht geladen werden: {ex.Message}");
                return false;
            }

            var translator = config.Translator ?? new TranslatorConfig();

            string modeInput = (translator.Mode ?? "all").ToLowerInvariant();
            string config_ = (CurrentConfiguration ?? "Debug").ToLowerInvariant();

            var activeModes = modeInput.Split(',')
                .Select(m => m.Trim())
                .ToList();

            Log.LogMessage(MessageImportance.High, $"[AI-Translator] Mode check: Config={config_}, IsPublish={IsPublishing}, Active modes=[{string.Join(", ", activeModes)}]");

            bool shouldRun = false;

            if (activeModes.Contains("all"))
            {
                shouldRun = true;
            }
            else
            {
                if (activeModes.Contains("debug") && config_ == "debug" && !IsPublishing) shouldRun = true;
                if (activeModes.Contains("release") && config_ == "release" && !IsPublishing) shouldRun = true;
                if (activeModes.Contains("publish") && IsPublishing) shouldRun = true;
            }

            if (!shouldRun)
            {
                Log.LogMessage(MessageImportance.High,
                    $"🤖 [AI-Translator] Skipped. The current state (Config={CurrentConfiguration}, Publish={IsPublishing}) " +
                    $"is not included in the allowed modes '{translator.Mode}'.");
                return true;
            }

            Log.LogMessage(MessageImportance.High, "🤖 [AI-Translator] Mode condition met. Starting analysis...");

            if (!string.IsNullOrEmpty(translator.Context))
            {
                Log.LogMessage(MessageImportance.High, $"[AI-Translator] App context received: {translator.Context}");
            }
            if (!string.IsNullOrEmpty(translator.GlossaryPath))
            {
                Log.LogMessage(MessageImportance.High, $"[AI-Translator] Glossary path received: {translator.GlossaryPath}");
            }

            Log.LogMessage(MessageImportance.High, "🤖 AI-Resx-Translator: Starting analysis...");

            var collectedIssues = new List<string>();
            var orchestrator = new ResxTranslationOrchestrator();

            var result = orchestrator.ProcessProject(ProjectDir, translator, logInfo: message => {
                    if (string.IsNullOrWhiteSpace(message)) return;

                    bool isError = message.Contains("[AI-Translator Error]") || message.Contains("[AI-Translator CRITICAL]");
                    bool isWarning = message.Contains("[AI-Translator Warning]") ||
                                     (message.Contains("[AI-Translator]") && message.Contains("No <SupportedLanguage> tag found"));

                    if (isError)
                    {
                        string cleanMessage = message.Replace("[AI-Translator Error]", "").Replace("[AI-Translator CRITICAL]", "").Trim();
                        Log.LogError($"AiTranslator: {cleanMessage}");
                        collectedIssues.Add($"[ERROR] {cleanMessage}");
                    }
                    else if (isWarning)
                    {
                        string cleanMessage = message.Replace("[AI-Translator Warning]", "").Replace("[AI-Translator]", "").Trim();
                        Log.LogWarning($"AiTranslator: {cleanMessage}");
                        collectedIssues.Add($"[WARNING] {cleanMessage}");
                    }
                    else
                    {
                        Log.LogMessage(MessageImportance.High, message);
                    }
                }).GetAwaiter().GetResult();

            // var result = orchestrator.ProcessProject(ProjectDir, translator, logInfo: message => { ... }).GetAwaiter().GetResult();

            //bool isWarning = message.Contains("[AI-Translator Warning]");

            if (!result.Success)
            {
                Log.LogError($"AiTranslator fatal error: {result.ErrorMessage}");
                collectedIssues.Add($"[FATAL ERROR] {result.ErrorMessage}");
            }

            var uniqueIssues = collectedIssues.Distinct().ToList();

            if (uniqueIssues.Count > 0)
            {
                TryOpenSummaryLog(uniqueIssues);
            }

            return result.Success;
        }

        private void TryOpenSummaryLog(List<string> issues) { /* unverändert */ }
    }
}