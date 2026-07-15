using UnityEngine;
using UnityEngine.Rendering;

namespace BuildingVolumes.Player
{
    public class MeshletCreator : MonoBehaviour
    {
        [SerializeField]
        int quadCount = 2000;

        //Fallback save location, used only when this component's MeshFilter isn't
        //already pointing at a saved mesh asset to overwrite.
        const string defaultAssetPath = "Packages/com.buildingvolumes.geometry_sequence_player/Runtime/Prefabs/Resources/Meshlet.asset";

        void Start()
        {
            MeshFilter meshFilter = GetComponent<MeshFilter>();
            if (meshFilter == null)
            {
                UnityEngine.Debug.LogError("No mesh filter found!");
                return;
            }

            Mesh meshlet = new Mesh();
            FillMesh(meshlet);
            meshFilter.sharedMesh = meshlet;
        }

        void FillMesh(Mesh meshlet)
        {
            Vector3 vertice1 = new Vector3(-0.5f, 0.5f, 0f);
            Vector3 vertice2 = new Vector3(0.5f, 0.5f, 0f);
            Vector3 vertice3 = new Vector3(0.5f, -0.5f, 0f);
            Vector3 vertice4 = new Vector3(-0.5f, -0.5f, 0f);

            Vector2 uv1 = new Vector2(0, 1);
            Vector2 uv2 = new Vector2(1, 1);
            Vector2 uv3 = new Vector2(1, 0);
            Vector2 uv4 = new Vector2(0, 0);

            Vector3[] vertices = new Vector3[quadCount * 4];
            Vector2[] uvs = new Vector2[quadCount * 4];
            int[] indices = new int[quadCount * 6];

            for (int i = 0; i < quadCount; i++)
            {
                vertices[i * 4 + 0] = vertice1;
                vertices[i * 4 + 1] = vertice2;
                vertices[i * 4 + 2] = vertice3;
                vertices[i * 4 + 3] = vertice4;

                uvs[i * 4 + 0] = uv1;
                uvs[i * 4 + 1] = uv2;
                uvs[i * 4 + 2] = uv3;
                uvs[i * 4 + 3] = uv4;

                indices[i * 6 + 0] = i * 4 + 0;
                indices[i * 6 + 1] = i * 4 + 1;
                indices[i * 6 + 2] = i * 4 + 3;
                indices[i * 6 + 3] = i * 4 + 1;
                indices[i * 6 + 4] = i * 4 + 2;
                indices[i * 6 + 5] = i * 4 + 3;
            }

            meshlet.Clear();
            //32-bit indices: lifts the ~16383-quad (65535-vertex) ceiling of the default UInt16 format.
            meshlet.indexFormat = IndexFormat.UInt32;
            meshlet.vertices = vertices;
            meshlet.SetUVs(0, uvs);
            meshlet.triangles = indices;
            meshlet.RecalculateBounds();
        }

#if UNITY_EDITOR
        [ContextMenu("Bake And Save Meshlet Asset")]
        void BakeAndSaveAsset()
        {
            MeshFilter meshFilter = GetComponent<MeshFilter>();

            string path = defaultAssetPath;
            if (meshFilter != null && meshFilter.sharedMesh != null)
            {
                string existing = UnityEditor.AssetDatabase.GetAssetPath(meshFilter.sharedMesh);
                if (!string.IsNullOrEmpty(existing))
                    path = existing;
            }

            Mesh target = UnityEditor.AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (target == null)
            {
                target = new Mesh { name = "Meshlet" };
                FillMesh(target);
                UnityEditor.AssetDatabase.CreateAsset(target, path);
            }
            else
            {
                //Repopulate the existing asset in place so its GUID stays stable and
                //the Meshlet prefab's mesh reference keeps resolving.
                FillMesh(target);
                UnityEditor.EditorUtility.SetDirty(target);
            }

            UnityEditor.AssetDatabase.SaveAssets();

            if (meshFilter != null)
                meshFilter.sharedMesh = target;

            UnityEngine.Debug.Log($"Baked meshlet: {quadCount} quads ({quadCount * 4} verts) -> {path}");
        }
#endif
    }
}
