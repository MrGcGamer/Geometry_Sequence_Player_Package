using UnityEngine;
using UnityEngine.Rendering;

namespace BuildingVolumes.Player
{
  /// <summary>
  /// Shared base for renderers that draw a pointcloud sequence as MeshTopology.Points
  /// (one vertex per point, no billboard quad, no compute pass).
  /// Subclasses supply SetFrame and, optionally, ConfigureMesh for their data path.
  /// </summary>
  public abstract class PointcloudPointsRendererBase : PointcloudRendererBase
  {
    protected Mesh pcMesh;

    MeshFilter pcMeshFilter;
    MeshRenderer pcMeshRenderer;

    //Shared default material/shader for every Points-topology path. Lives under a render-path-neutral
    //Resources/Points folder (not Draco) because the shader is codec-agnostic - Draco just happens to
    //have wired it first.
    const string defaultMaterialResourcePath = "Points/Pointcloud_Points";

    protected const MeshUpdateFlags meshUpdateFlags = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers;

    protected override void OnSetup(SequenceConfiguration config)
    {
      pcMesh = CreateStreamMesh(out pcMeshFilter, out pcMeshRenderer);

      ConfigureMesh(config);

      RegisterRenderer(pcMeshRenderer);
    }

    /// <summary>
    /// Per-source mesh layout. The raw-buffer path declares the interleaved vertex layout and a
    /// sequential index buffer here; a path that lets Mesh.ApplyAndDisposeWritableMeshData define
    /// the layout per frame needs nothing.
    /// </summary>
    protected virtual void ConfigureMesh(SequenceConfiguration config) { }

    protected override Material LoadDefaultMaterial()
    {
      Material mat = Resources.Load(defaultMaterialResourcePath, typeof(Material)) as Material;

      if (!mat)
        Debug.LogError("Could not load default pointcloud material at Resources/" + defaultMaterialResourcePath);

      return mat;
    }
  }
}
