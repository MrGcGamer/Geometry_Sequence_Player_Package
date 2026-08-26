using UnityEngine;
using System.Collections.Generic;

namespace BuildingVolumes.Player
{
  public interface IMeshSequenceRenderer
  {
    public bool Setup(Transform parent, SequenceConfiguration config);
    public void RenderFrame(Frame frame);
    public void ApplySingleTexture(Frame frame);
    public void ChangeMaterial(Material material, bool instantiateMaterial);
    public void SetOpacity(float opacity);
    public void ChangeMaterial(Material material, GeometrySequenceStream.MaterialProperties properties, List<string> customProperties, bool instantiateMaterial);
    public void Show();
    public void Hide();
    public void Dispose();

    public bool IsDisposed();

#if UNITY_EDITOR
    public void EndEditorLife();
#endif

  }
}

