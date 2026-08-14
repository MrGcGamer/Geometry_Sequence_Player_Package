using UnityEngine;
using UnityEngine.Rendering;

namespace BuildingVolumes.Player
{
  /// <summary>
  /// Draws the cloud as one quad mesh sized for the whole sequence, each quad reading its point out
  /// of the render textures the base class fills.
  /// </summary>
  public class PointcloudRendererRT : PointcloudRendererRTBase
  {
    MeshFilter pcMeshFilter;
    MeshRenderer pcMeshRenderer;

    //The quad mesh is allocated once for the whole sequence, but only the current frame's points
    //are drawn. The sequence bounds are cached rather than read back off the mesh: SetSubMesh writes
    //the same bounds it would be read from, so sourcing them there makes the value self-referential
    //and one bad write - the empty submesh the first call installs - poisons it for the whole
    //sequence. This copy comes from the sequence config and is never written from the mesh.
    Bounds sequenceBounds;
    int visiblePointCount = -1;

    protected override string StreamObjectName => "PointcloudRenderer";
    protected override string ComputeShaderResourcePath => "ShaderGraph/Pointcloud_Shadergraph";

    protected override void CreateGeometry(SequenceConfiguration config)
    {
      sequenceBounds = config.GetBounds();

      Mesh mesh = CreateStreamMesh(out pcMeshFilter, out pcMeshRenderer);

      //The mesh consists out of simple quads, which we define here
      int quadCount = config.maxVertexCount;

      Vector3 vertice1 = new Vector3(-0.5f, 0.5f, 0f);
      Vector3 vertice2 = new Vector3(0.5f, 0.5f, 0f);
      Vector3 vertice3 = new Vector3(0.5f, -0.5f, 0f);
      Vector3 vertice4 = new Vector3(-0.5f, -0.5f, 0f);

      Vector3 normal = new Vector3(0, 0, 1);

      Vector2 uv1 = new Vector2(0, 1);
      Vector2 uv2 = new Vector2(1, 1);
      Vector2 uv3 = new Vector2(1, 0);
      Vector2 uv4 = new Vector2(0, 0);

      Vector3[] vertices = new Vector3[quadCount * 4];
      Vector3[] normals = new Vector3[1];
      Vector2[] uvs = new Vector2[quadCount * 4];
      int[] indices = new int[quadCount * 6];
      if (config.hasNormals)
        normals = new Vector3[quadCount * 4];

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

        if (config.hasNormals)
        {
          normals[i * 4 + 0] = normal;
          normals[i * 4 + 1] = normal;
          normals[i * 4 + 2] = normal;
          normals[i * 4 + 3] = normal;
        }

        indices[i * 6 + 0] = i * 4 + 0;
        indices[i * 6 + 1] = i * 4 + 1;
        indices[i * 6 + 2] = i * 4 + 3;
        indices[i * 6 + 3] = i * 4 + 1;
        indices[i * 6 + 4] = i * 4 + 2;
        indices[i * 6 + 5] = i * 4 + 3;
      }

      //Important, as we often deal with more than 16000 triangles
      mesh.indexFormat = IndexFormat.UInt32;

      mesh.vertices = vertices;
      mesh.triangles = indices;
      mesh.SetUVs(0, uvs);
      //Assigned after the vertices, which would otherwise derive bounds from the quad corners alone.
      mesh.bounds = sequenceBounds;
      if (config.hasNormals)
        mesh.normals = normals;

      RegisterRenderer(pcMeshRenderer);

      //Nothing is drawn until the first frame arrives: the point data render textures hold stale
      //data until then, and the opaque default material has no alpha clip to hide it.
      SetVisiblePointCount(0);
    }

    protected override int PrepareUpload(int pointCount)
    {
      SetVisiblePointCount(pointCount);

      //Only the rows this frame's points reach need uploading. The rows past them keep stale data,
      //which is harmless because the draw was just clamped to the same point count. Derived from
      //visiblePointCount rather than the argument: that one is the clamped value.
      return Mathf.CeilToInt(visiblePointCount / (float)rtResolution);
    }

    /// <summary>
    /// Restricts the draw to the points the current frame actually contains.
    /// </summary>
    /// <remarks>
    /// The quad mesh is sized once for the sequence's maximum point count, so without this every
    /// frame vertex-shades and rasterizes the unused tail as well - on a sequence whose point count
    /// varies that is pure waste. It is also what makes an opaque, non-alpha-clipped point material
    /// possible: the tail used to be hidden by the compute shader writing a zero alpha, which only
    /// works if something later clips on it.
    /// </remarks>
    void SetVisiblePointCount(int pointCount)
    {
      //Checked before the property is touched: sharedMesh throws once the MeshFilter is destroyed.
      if (pcMeshFilter == null || pcMeshFilter.sharedMesh == null)
        return;

      Mesh mesh = pcMeshFilter.sharedMesh;

      //Clamped against the mesh rather than the sequence's maxVertexCount it was built from: the mesh
      //is what the indices have to stay inside of.
      pointCount = Mathf.Clamp(pointCount, 0, mesh.vertexCount / 4);

      if (pointCount == visiblePointCount)
        return;

      visiblePointCount = pointCount;

      SubMeshDescriptor subMesh = new SubMeshDescriptor(0, pointCount * 6, MeshTopology.Triangles)
      {
        firstVertex = 0,
        vertexCount = pointCount * 4,
        //Supplied explicitly because SetSubMesh replaces the submesh bounds, and the vertex positions
        //are all zero-centered quad corners, so anything else collapses the culling volume.
        bounds = sequenceBounds
      };

      //DontNotifyMeshUsers is deliberately not passed: it leaves the MeshRenderer on the bounds it
      //cached when the vertices were assigned, which for this mesh is the 1x1x0 quad-corner box.
      mesh.SetSubMesh(0, subMesh, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
      mesh.bounds = sequenceBounds;

      //Set on the renderer as well, so culling and the editor's wireframe never depend on how mesh
      //bounds propagate through SetSubMesh. localBounds overrides the mesh's bounds outright.
      if (pcMeshRenderer)
        pcMeshRenderer.localBounds = sequenceBounds;
    }

    protected override Material LoadDefaultMaterial()
    {
      //Alpha-clipped circles, not the opaque squares this path briefly defaulted to. Dropping the
      //clip does hand occlusion back to the hardware - a discard disables hidden surface removal on
      //Apple's tile-based GPUs - but it costs round points, and point shape is not negotiable.
      string path = configuration.hasNormals
        ? "ShaderGraph/Pointcloud_Circles_Lit_Shadergraph"
        : "ShaderGraph/Pointcloud_Circles_Shadergraph";

      Material mat = Resources.Load(path, typeof(Material)) as Material;

      if (mat == null)
        Debug.LogError("Could not load default pointcloud material at Resources/" + path);

      return mat;
    }

    public override void Dispose()
    {
      visiblePointCount = -1;

      base.Dispose();
    }
  }
}
