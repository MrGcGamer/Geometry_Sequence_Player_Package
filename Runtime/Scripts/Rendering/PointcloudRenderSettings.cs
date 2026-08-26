using UnityEngine;

namespace BuildingVolumes.Player
{
  /// <summary>
  /// Everything a pointcloud renderer needs to come up in the right look: the material to draw with
  /// and the per-stream appearance values written into it.
  /// </summary>
  /// <remarks>
  /// Passed by value into <see cref="IPointCloudRenderer.Setup"/> rather than as a parameter list,
  /// because the material is created inside Setup: a value applied afterwards would show one frame
  /// of the wrong look, and the meshlet path would build its materials twice. Always start from
  /// <see cref="Default"/> - the zero struct means a fully transparent cloud.
  /// </remarks>
  public struct PointcloudRenderSettings
  {
    public float pointSize;
    public float emission;
    public float opacity;
    public Material material;
    public bool instantiateMaterial;

    public static PointcloudRenderSettings Default => new PointcloudRenderSettings
    {
      pointSize = 0.02f,
      emission = 1f,
      opacity = 1f,
      material = null,
      instantiateMaterial = true
    };
  }
}
