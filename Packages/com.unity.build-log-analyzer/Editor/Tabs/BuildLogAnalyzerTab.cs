using UnityEditor;

namespace BuildLogAnalyzer.Editor
{
    abstract class BuildLogAnalyzerTab
    {
        public abstract string TabName { get; }
        public virtual void OnEnable(EditorWindow window) { }
        public abstract void ParseLines(string[] lines);
        public abstract void DrawGUI(float contentWidth);
        public abstract void Clear();
        public virtual string GetStatusMessage() => string.Empty;
    }
}
