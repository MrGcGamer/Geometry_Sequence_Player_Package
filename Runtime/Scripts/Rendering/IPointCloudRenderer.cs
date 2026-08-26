using UnityEngine;
using Unity.Collections;

namespace BuildingVolumes.Player
{
  public interface IPointCloudRenderer
  {
    public void Setup(SequenceConfiguration configuration, Transform parent, PointcloudRenderSettings settings);
    public void SetFrame(Frame frame);
    public void SetPointSize(float size);
    public void SetPointEmission(float emission);
    public void SetOpacity(float opacity);
    public void SetPointcloudMaterial(Material pointcloudMaterial, bool instantiateMaterial);
    public void SetPointcloudMaterial(Material pointcloudMaterial, float pointSize, float pointEmission, bool instantiateMaterial);

    public void Show();
    public void Hide();
    public void Dispose();

    public bool IsDisposed();
  }
}
