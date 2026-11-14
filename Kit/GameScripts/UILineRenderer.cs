using UnityEngine;
using UnityEngine.UI;

[ExecuteInEditMode]
public class UILineRenderer : Graphic
{
	[SerializeField]
	private float thickness = 10f;

	[SerializeField]
	private Line[] lines = new Line[0];

	protected override void OnPopulateMesh(VertexHelper mesh)
	{
		mesh.Clear();
		if (lines == null)
			return;
		foreach (var line in lines)
		{
			if (line == null || line.Start == null || line.End == null)
				continue;
			var from = (Vector2)rectTransform.InverseTransformPoint(line.Start.position);
			var to = (Vector2)rectTransform.InverseTransformPoint(line.End.position);
			var side = Vector2.Perpendicular((to - from).normalized) * (thickness * 0.5f);
			var first = mesh.currentVertCount;
			AddVertex(mesh, from - side);
			AddVertex(mesh, from + side);
			AddVertex(mesh, to + side);
			AddVertex(mesh, to - side);
			mesh.AddTriangle(first, first + 1, first + 2);
			mesh.AddTriangle(first + 2, first + 3, first);
		}
	}

	private void AddVertex(VertexHelper mesh, Vector2 position)
	{
		var vertex = UIVertex.simpleVert;
		vertex.position = position;
		vertex.color = color;
		mesh.AddVert(vertex);
	}

	private void Update()
	{
		if (lines == null)
			return;
		foreach (var line in lines)
		{
			if (line != null && (line.Start != null && line.Start.hasChanged || line.End != null && line.End.hasChanged))
			{
				SetVerticesDirty();
				return;
			}
		}
	}
}
