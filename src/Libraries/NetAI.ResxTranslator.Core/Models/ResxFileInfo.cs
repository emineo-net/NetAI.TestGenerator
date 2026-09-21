namespace NetAI.ResxTranslator.Core.Models
{
    public class ResxFileInfo(string filePath)
    {
        public string FilePath { get; } = filePath;
        public string FileName => Path.GetFileName(FilePath);
        public List<ResxEntry> Entries { get; set; } = new();
    }
}