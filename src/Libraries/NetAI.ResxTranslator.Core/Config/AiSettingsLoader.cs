using Newtonsoft.Json;

namespace NetAI.ResxTranslator.Core.Config
{
    public static class AiSettingsLoader
    {
        public static AiTestingConfig Load(string projectDir)
        {
            if (string.IsNullOrWhiteSpace(projectDir))
                throw new ArgumentException("ProjectDir darf nicht leer sein.", nameof(projectDir));

            var path = Path.Combine(projectDir, "aisettings.json");

            if (!File.Exists(path))
                throw new FileNotFoundException($"aisettings.json wurde in '{projectDir}' nicht gefunden.", path);

            var json = File.ReadAllText(path);

            var config = JsonConvert.DeserializeObject<AiTestingConfig>(json);

            if (config is null)
                throw new InvalidOperationException($"aisettings.json unter '{path}' konnte nicht geparst werden.");

            return config;
        }
    }
}