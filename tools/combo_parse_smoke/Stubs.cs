namespace SifuMovesetEditor
{
    public static class ErrorLog
    {
        public static void Write(string category, System.Exception ex)
        {
            System.Console.Error.WriteLine($"[{category}] {ex.Message}");
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
            var leaf = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
            return leaf.Equals("Content", System.StringComparison.OrdinalIgnoreCase)
                ? path
                : System.IO.Path.Combine(path, "Content");
        }
    }
}
