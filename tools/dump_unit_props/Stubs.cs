using System;
using System.IO;

namespace SifuMovesetEditor
{
    public static class ErrorLog
    {
        public static void Write(string category, Exception ex)
        {
            Console.Error.WriteLine($"[{category}] {ex.Message}");
        }
    }
}

namespace SifuMovesetEditor.Setup
{
    public static class ContentDetector
    {
        public static string ResolveContentDir(string? path)
        {
            if (string.IsNullOrEmpty(path))
                return path ?? "";
            var leaf = Path.GetFileName(path.TrimEnd('\\', '/'));
            return leaf.Equals("Content", StringComparison.OrdinalIgnoreCase)
                ? path
                : Path.Combine(path, "Content");
        }
    }
}
