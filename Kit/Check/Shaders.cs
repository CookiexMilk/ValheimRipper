using UnityEditor;
using UnityEngine;

namespace ValheimRipperCheck
{
	static class Shaders
	{
		static void Run()
		{
			var fine = 0;
			var broken = 0;
			foreach (var guid in AssetDatabase.FindAssets("t:Shader", new[] { "Assets" }))
			{
				var shader = AssetDatabase.LoadAssetAtPath<Shader>(AssetDatabase.GUIDToAssetPath(guid));
				if (shader == null)
					continue;
				if (!ShaderUtil.ShaderHasError(shader))
				{
					fine++;
					continue;
				}
				broken++;
				Debug.Log($"[ValheimRipper] Shader error: {shader.name}");
			}
			Debug.Log(broken == 0 ? $"[ValheimRipper] All {fine} shaders compile." : $"[ValheimRipper] {broken} shaders have errors ({fine} fine).");
		}
	}
}
