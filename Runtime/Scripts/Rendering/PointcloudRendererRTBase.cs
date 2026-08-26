using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace BuildingVolumes.Player
{
  /// <summary>
  /// Shared base for the render paths that stage a frame's points in render textures: a compute
  /// shader unpacks the interleaved source buffer into a positions and a colors texture (plus
  /// normals where the sequence has them), and the vertex shader reads one point back out of them
  /// per quad. Subclasses only build the geometry that samples those textures - one mesh sized for
  /// the whole sequence in <see cref="PointcloudRendererRT"/>, a batch of meshlets in
  /// <see cref="PointcloudRendererRT_Meshlet"/>.
  /// </summary>
  public abstract class PointcloudRendererRTBase : PointcloudRendererBase
  {
    protected ComputeShader computeShaderRT;
    protected RenderTexture rtPositions;
    protected RenderTexture rtColors;
    protected RenderTexture rtNormals;
    protected int rtResolution;

    GraphicsBuffer[] pointSourceBuffers;
    int bufferIndex;

    //Enough of the last upload to repeat it. Opacity reaches the graph through the colour texture,
    //so changing it has to rewrite that texture; waiting for the next frame would leave a paused or
    //thumbnailed cloud showing the old value.
    int lastPointCount;
    int lastGroupSizeY;

    //Compute shader property IDs
    static readonly int rtPositionsID = Shader.PropertyToID("_RTPositions");
    static readonly int rtColorsID = Shader.PropertyToID("_RTColors");
    static readonly int rtNormalsID = Shader.PropertyToID("_RTNormals");
    static readonly int rtStrideID = Shader.PropertyToID("_RTStride");


    //Vertex/Fragment shader property IDs
    static readonly int rtResolutionID = Shader.PropertyToID("_RTResolution");
    static readonly int rtPositionSourceID = Shader.PropertyToID("_PositionSourceRT");
    static readonly int rtNormalSourceID = Shader.PropertyToID("_NormalSourceRT");
    static readonly int rtColorSourceID = Shader.PropertyToID("_ColorSourceRT");

    /// <summary>Resources path of the compute shader that unpacks points into the render textures.</summary>
    protected abstract string ComputeShaderResourcePath { get; }

    /// <summary>
    /// How many source buffers to rotate through. Three by default, because a dispatch is not
    /// guaranteed to have consumed the buffer before the next frame overwrites it. The Polyspatial
    /// path keeps one, where the memory costs more than the overlap saves.
    /// </summary>
    protected virtual int SourceBufferCount => 3;

    /// <summary>
    /// The compute kernel that unpacks this sequence's points. Fixed for the sequence, since it is
    /// only the presence of normals that picks between the two.
    /// </summary>
    protected int kernel;

    protected override void OnSetup(SequenceConfiguration config)
    {
      if (computeShaderRT == null)
        computeShaderRT = Resources.Load(ComputeShaderResourcePath, typeof(ComputeShader)) as ComputeShader;

      if (computeShaderRT == null)
      {
        Debug.LogError("Could not load the pointcloud compute shader at Resources/" + ComputeShaderResourcePath);
        return;
      }

      //A square texture that every point of the longest frame fits into, one texel per point
      rtResolution = Mathf.CeilToInt(Mathf.Sqrt(config.maxVertexCount));

      rtPositions = CreatePointTexture(GraphicsFormat.R16G16B16A16_SFloat);
      rtColors = CreatePointTexture(GraphicsFormat.R8G8B8A8_UNorm);
      if (config.hasNormals)
        rtNormals = CreatePointTexture(GraphicsFormat.R16G16B16A16_SFloat);

      int stride = SourceByteStride;

      pointSourceBuffers = new GraphicsBuffer[SourceBufferCount];
      for (int i = 0; i < pointSourceBuffers.Length; i++)
        pointSourceBuffers[i] = new GraphicsBuffer(GraphicsBuffer.Target.Raw, GraphicsBuffer.UsageFlags.LockBufferForWrite, rtResolution * rtResolution, stride);

      bufferIndex = 0;

      //The compute shaders declare the plain kernel first, the normals one second.
      kernel = config.hasNormals ? 1 : 0;

      computeShaderRT.SetInt(rtStrideID, rtResolution);
      computeShaderRT.SetInt(sourceStrideID, stride);
      computeShaderRT.SetTexture(kernel, rtPositionsID, rtPositions);
      computeShaderRT.SetTexture(kernel, rtColorsID, rtColors);
      if (config.hasNormals)
        computeShaderRT.SetTexture(kernel, rtNormalsID, rtNormals);

      CreateGeometry(config);
    }

    RenderTexture CreatePointTexture(GraphicsFormat format)
    {
      RenderTexture texture = new RenderTexture(rtResolution, rtResolution, 0, format);
      texture.enableRandomWrite = true;
      texture.filterMode = FilterMode.Point;
      texture.Create();
      return texture;
    }

    /// <summary>
    /// Build the geometry that samples the render textures. They and the source buffers already
    /// exist, so a MeshRenderer registered from here binds a fully wired material.
    /// </summary>
    protected abstract void CreateGeometry(SequenceConfiguration config);

    public override void SetFrame(Frame frame)
    {
      //pointSourceBuffers stays null when OnSetup bailed out on a missing compute shader, which
      //leaves the renderer ready but with nothing to upload into.
      if (!ready || isDisposed || pointSourceBuffers == null)
        return;

      CompleteFrameJobs(frame);

      bufferIndex = (bufferIndex + 1) % pointSourceBuffers.Length;
      GraphicsBuffer pointSourceBuffer = pointSourceBuffers[bufferIndex];

      //Locking the buffer is faster than GraphicsBuffer.SetData
      NativeArray<byte> pointdataGPU = pointSourceBuffer.LockBufferForWrite<byte>(0, frame.geoJob.vertexBuffer.Length);
      frame.geoJob.vertexBuffer.CopyTo(pointdataGPU);
      pointSourceBuffer.UnlockBufferAfterWrite<byte>(frame.geoJob.vertexBuffer.Length);

      int pointCount = frame.geoJob.vertexCount;

      computeShaderRT.SetBuffer(kernel, pointSourceBufferID, pointSourceBuffer);

      lastPointCount = pointCount;
      lastGroupSizeY = Mathf.Max(1, Mathf.CeilToInt(PrepareUpload(pointCount) / 32f));

      Upload();
    }

    /// <summary>
    /// Run the unpack kernel over the buffer that is currently bound, writing the render textures
    /// the material samples.
    /// </summary>
    /// <remarks>
    /// The per-dispatch uniforms are re-set every time rather than once at setup, because the
    /// compute shader is the shared Resources asset, not a copy: a second stream on the same render
    /// path writes its own values into it between one of our dispatches and the next.
    /// </remarks>
    void Upload()
    {
      computeShaderRT.SetInt(pointCountID, lastPointCount);
      computeShaderRT.SetInt(rtStrideID, rtResolution);
      computeShaderRT.SetInt(sourceStrideID, SourceByteStride);
      //Deliberately 1, not currentOpacity: the graphs clip the circle out of each quad against a
      //fixed 0.5 threshold on this very channel, so anything less takes the cloud apart. See
      //ApplyOpacity.
      computeShaderRT.SetFloat(alphaID, 1f);
      computeShaderRT.SetTexture(kernel, rtPositionsID, rtPositions);
      computeShaderRT.SetTexture(kernel, rtColorsID, rtColors);
      if (rtNormals != null)
        computeShaderRT.SetTexture(kernel, rtNormalsID, rtNormals);

      computeShaderRT.Dispatch(kernel, Mathf.CeilToInt(rtResolution / 32f), lastGroupSizeY, 1);

      OnFrameUploaded();
    }

    /// <summary>
    /// Runs once per frame just before the upload dispatch, and does two things: it lets the
    /// subclass point its geometry at the <paramref name="pointCount"/> points this frame carries,
    /// and it returns how many rows of the render textures the dispatch has to write. The textures
    /// hold one point per texel, filled row by row, so one row is rtResolution points.
    ///
    /// The default writes every row, which is what a path needs when its geometry always draws every
    /// quad it owns: any row still holding the previous frame's points would otherwise show up as
    /// leftover points on screen.
    ///
    /// A path that shrinks its draw can write only the rows the drawn geometry reaches and let the
    /// rest go stale, because nothing reads them. Returning that row count from the same call is what
    /// keeps it in step with the clamp the override just applied - and the rows have to cover the
    /// geometry that is still drawn, which is not the same as covering <paramref name="pointCount"/>
    /// when the clamp is coarser than one point.
    /// </summary>
    protected virtual int PrepareUpload(int pointCount) => rtResolution;

    /// <summary>Called after the upload dispatch, for platforms that have to be told the textures changed.</summary>
    protected virtual void OnFrameUploaded() { }

    /// <summary>
    /// Opacity is not applied on these paths yet. The material is left in its opaque, alpha-clipped
    /// state and the compute shader keeps writing a fully opaque colour texture, so the render is
    /// exactly what it has always been; a request below 1 warns instead of half-working.
    /// </summary>
    /// <remarks>
    /// The graphs cannot fade without a change inside them, and it is a one-node change. Their Alpha
    /// is <c>colourRT.a x circleMask</c>, and the circle exists only because the Alpha Clip Threshold
    /// discards everything under 0.5 - the mask is a soft, distance-like value, not a binary one, so
    /// without the clip each point draws as a soft square and the cloud turns into a colourless white
    /// haze. Measured: correct down to opacity 0.55, gone entirely at 0.45, because the threshold is
    /// a constant 0.5 baked into the graph rather than a material property.
    ///
    /// So this one channel cannot carry both the fade and a clip against a fixed threshold. The fix is
    /// to make the threshold scale with the opacity: feed <c>0.5 x opacity</c> into the Alpha Clip
    /// Threshold block. The test then reduces to <c>mask > 0.5</c> - the same circle at every opacity -
    /// while the blend alpha stays equal to the opacity. One property and one multiply per graph.
    /// </remarks>
    protected override void ApplyOpacity(int rendererIndex)
    {
      Material mat = GetMaterial(rendererIndex);

      if (mat == null || currentOpacity >= 1f || warnedAboutGraphOpacity)
        return;

      warnedAboutGraphOpacity = true;
      Debug.LogWarning("The " + GetType().Name + " render path cannot fade yet: '" + mat.shader.name +
        "' derives its round points from an alpha clip at a fixed 0.5 threshold, which the opacity would " +
        "have to pass through. Use the Points render path for a fadeable cloud. See " +
        nameof(PointcloudRendererRTBase) + ".ApplyOpacity for the graph change that lifts this.", this);
    }

    bool warnedAboutGraphOpacity;

    protected override void ConfigureMaterial(Material mat, int rendererIndex)
    {
      mat.SetFloat(rtResolutionID, rtResolution);
      mat.SetTexture(rtPositionSourceID, rtPositions);
      mat.SetTexture(rtColorSourceID, rtColors);

      if (rtNormals != null)
        mat.SetTexture(rtNormalSourceID, rtNormals);
    }

    protected override void ReleaseResources()
    {
      if (rtPositions != null)
        DestroyImmediate(rtPositions);
      if (rtColors != null)
        DestroyImmediate(rtColors);
      if (rtNormals != null)
        DestroyImmediate(rtNormals);

      rtPositions = null;
      rtColors = null;
      rtNormals = null;

      if (pointSourceBuffers != null)
      {
        for (int i = 0; i < pointSourceBuffers.Length; i++)
          if (pointSourceBuffers[i] != null)
            pointSourceBuffers[i].Dispose();

        pointSourceBuffers = null;
      }
    }
  }
}
