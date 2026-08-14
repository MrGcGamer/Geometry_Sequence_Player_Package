using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace BuildingVolumes.Player
{
  /// <summary>
  /// Renders a pointcloud sequence as MeshTopology.Points (one vertex per point).
  /// Consumes the universal interleaved vertex buffer (pos f32x3 [+ normals] + color RGBA8).
  /// </summary>
  public class PointcloudRendererPoints : PointcloudPointsRendererBase
  {
    int pointSourceCount;
    int lastBufferUpdateFrame = -1;

    protected override string StreamObjectName => "PointcloudRendererPoints";

    protected override void ConfigureMesh(SequenceConfiguration config)
    {
      VertexAttributeDescriptor vp = new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3);
      VertexAttributeDescriptor vn = new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3);
      VertexAttributeDescriptor vc = new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4);

      if (config.hasNormals)
        pcMesh.SetVertexBufferParams(config.maxVertexCount, vp, vn, vc);
      else
        pcMesh.SetVertexBufferParams(config.maxVertexCount, vp, vc);

      //Points topology uses one index per vertex; a sequential index buffer maps index i -> vertex i
      pcMesh.SetIndexBufferParams(config.maxVertexCount, IndexFormat.UInt32);
      NativeArray<uint> indices = new NativeArray<uint>(config.maxVertexCount, Allocator.Temp);
      for (int i = 0; i < indices.Length; i++)
        indices[i] = (uint)i;
      pcMesh.SetIndexBufferData(indices, 0, 0, indices.Length, meshUpdateFlags);
      indices.Dispose();

      pcMesh.subMeshCount = 1;
      pcMesh.SetSubMesh(0, new SubMeshDescriptor(0, 0, MeshTopology.Points), meshUpdateFlags);
    }

    public override void SetFrame(Frame frame)
    {
      if (!ready || isDisposed)
        return;
      if (lastBufferUpdateFrame == Time.frameCount)
        return;

      CompleteFrameJobs(frame);

      pointSourceCount = frame.geoJob.vertexCount;

      //Upload only the used portion of the interleaved buffer, then draw it as points
      pcMesh.SetVertexBufferData(frame.vertexBufferRaw, 0, 0, pointSourceCount * SourceByteStride, 0, meshUpdateFlags);
      pcMesh.SetSubMesh(0, new SubMeshDescriptor(0, pointSourceCount, MeshTopology.Points), meshUpdateFlags);
      pcMesh.bounds = configuration.GetBounds();

      lastBufferUpdateFrame = Time.frameCount;
    }
  }
}
