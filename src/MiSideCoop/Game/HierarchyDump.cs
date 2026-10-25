using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MiSideCoop.Game;

// Writes every loaded scene's object tree with component types, for reverse engineering.
internal static class HierarchyDump
{
    public static int Write(string path, int maxDepth = 64)
    {
        var sb = new StringBuilder();
        int count = 0;
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var scene = SceneManager.GetSceneAt(i);
            sb.Append("# scene ").Append(scene.name).Append(" (index ").Append(scene.buildIndex).AppendLine(")");
            foreach (var root in scene.GetRootGameObjects()) count += Walk(root.transform, 0, maxDepth, sb);
        }
        // Objects kept across loads live in a hidden scene that only a DontDestroyOnLoad object can reach.
        var probe = new GameObject("coop-ddol-probe");
        Object.DontDestroyOnLoad(probe);
        var ddol = probe.scene;
        sb.AppendLine("# scene DontDestroyOnLoad");
        foreach (var root in ddol.GetRootGameObjects())
            if (root != probe) count += Walk(root.transform, 0, maxDepth, sb);
        Object.Destroy(probe);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
        File.WriteAllText(path, sb.ToString());
        return count;
    }

    private static int Walk(Transform t, int depth, int maxDepth, StringBuilder sb)
    {
        sb.Append(' ', depth * 2).Append(t.name);
        if (!t.gameObject.activeSelf) sb.Append(" [off]");
        sb.Append("  {");
        var comps = t.GetComponents<Component>();
        for (int i = 0; i < comps.Length; i++)
        {
            if (comps[i] == null) { sb.Append("<missing>,"); continue; }
            if (i > 0) sb.Append(',');
            sb.Append(comps[i].GetIl2CppType().Name);
        }
        var p = t.position;
        sb.Append("}  @").Append(p.x.ToString("0.00")).Append(',').Append(p.y.ToString("0.00")).Append(',')
          .Append(p.z.ToString("0.00")).Append(" layer=").Append(t.gameObject.layer).AppendLine();
        int count = 1;
        if (depth >= maxDepth) return count;
        for (int i = 0; i < t.childCount; i++) count += Walk(t.GetChild(i), depth + 1, maxDepth, sb);
        return count;
    }
}
