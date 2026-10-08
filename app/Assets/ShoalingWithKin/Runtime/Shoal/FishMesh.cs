using System.Collections.Generic;
using UnityEngine;

namespace ShoalingWithKin
{
    /// <summary>
    /// A unit-length fish, nose at +Z (0.5) and tail fin at -Z (-0.5), so LookRotation(velocity)
    /// points it where it swims. uv.x runs nose (0) to tail (1) for the shader's tail wiggle.
    /// </summary>
    public static class FishMesh
    {
        const int Sides = 6;

        // (z, half-width, half-height) of each body ring, nose to tail stalk.
        static readonly Vector3[] Rings =
        {
            new(0.27f, 0.055f, 0.09f),
            new(0.08f, 0.085f, 0.15f),
            new(-0.14f, 0.05f, 0.09f),
        };

        public static Mesh Create()
        {
            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();

            int nose = Add(vertices, uvs, new Vector3(0f, 0f, 0.5f));
            var ringStart = new int[Rings.Length];
            for (int r = 0; r < Rings.Length; r++)
            {
                ringStart[r] = vertices.Count;
                for (int s = 0; s < Sides; s++)
                {
                    float a = s * Mathf.PI * 2f / Sides;
                    Add(vertices, uvs, new Vector3(Mathf.Cos(a) * Rings[r].y, Mathf.Sin(a) * Rings[r].z, Rings[r].x));
                }
            }
            int stalk = Add(vertices, uvs, new Vector3(0f, 0f, -0.3f));

            for (int s = 0; s < Sides; s++)
            {
                int n = (s + 1) % Sides;
                // Clockwise seen from outside, so RecalculateNormals points them outward.
                triangles.AddRange(new[] { nose, ringStart[0] + s, ringStart[0] + n });
                for (int r = 0; r + 1 < Rings.Length; r++)
                {
                    int a = ringStart[r], b = ringStart[r + 1];
                    triangles.AddRange(new[] { a + s, b + s, a + n, b + s, b + n, a + n });
                }
                int last = ringStart[Rings.Length - 1];
                triangles.AddRange(new[] { last + s, stalk, last + n });
            }

            // Tail fin: a vertical fan, drawn double-sided by the shader (Cull Off).
            int root = Add(vertices, uvs, new Vector3(0f, 0f, -0.26f));
            int top = Add(vertices, uvs, new Vector3(0f, 0.16f, -0.5f));
            int notch = Add(vertices, uvs, new Vector3(0f, 0f, -0.42f));
            int bottom = Add(vertices, uvs, new Vector3(0f, -0.16f, -0.5f));
            triangles.AddRange(new[] { root, top, notch, root, notch, bottom });

            var mesh = new Mesh { name = "Fish" };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static int Add(List<Vector3> vertices, List<Vector2> uvs, Vector3 v)
        {
            vertices.Add(v);
            uvs.Add(new Vector2(0.5f - v.z, 0.5f + v.y * 3f));
            return vertices.Count - 1;
        }
    }
}
