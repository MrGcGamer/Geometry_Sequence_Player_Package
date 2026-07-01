#if DRACO_AVAILABLE
using UnityEngine;

namespace BuildingVolumes.Player
{
  /// <summary>
  /// Renders Draco-compressed pointcloud frames. Draco decodes into a Mesh.MeshDataArray ahead of
  /// playback (see BufferedGeometryReader.DecodeDracoFrameAsync); this renderer applies that data
  /// via the base class's Points-topology mesh.
  /// </summary>
  public class DracoPointcloudRenderer : PointcloudPointsRendererBase
  {
    protected override string StreamObjectName => "DracoPointcloudRenderer";

    public override void SetFrame(Frame frame)
    {
      if (!buffersInitialized || isDisposed)
        return;

      //Decode runs ahead of playback in the reader; only apply once the data is ready and valid
      if (!frame.dracoReady || !frame.dracoMeshDataValid)
        return;

      //ApplyAndDispose consumes (and disposes) the MeshDataArray. Clear the flag so the reader's
      //teardown / slot reuse won't dispose it a second time.
      Mesh.ApplyAndDisposeWritableMeshData(frame.dracoMeshData, pcMesh, meshUpdateFlags);
      frame.dracoMeshDataValid = false;

      pcMesh.bounds = configuration.GetBounds();
    }
  }
}
#endif
