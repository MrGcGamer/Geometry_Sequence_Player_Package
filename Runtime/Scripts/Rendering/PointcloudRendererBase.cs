using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace BuildingVolumes.Player
{
  /// <summary>
  /// Shared base for all pointcloud renderers. It owns everything that does not depend on how the
  /// points reach the GPU: the setup/teardown lifecycle, the child GameObject the geometry is
  /// parented to, the set of MeshRenderers drawing the cloud, and the material, point size,
  /// emission and opacity bookkeeping.
  ///
  /// A subclass builds its geometry and GPU resources in <see cref="OnSetup"/>, hands every
  /// MeshRenderer it creates to <see cref="RegisterRenderer"/>, and frees what it allocated in
  /// <see cref="ReleaseResources"/>. Registration is what applies the current material, point size,
  /// emission, opacity and visibility, so renderers created after Setup returns (the meshlet path builds
  /// its meshlets over several frames) come up in exactly the same state as the rest.
  /// </summary>
  public abstract class PointcloudRendererBase : MonoBehaviour, IPointCloudRenderer
  {
    protected SequenceConfiguration configuration;

    /// <summary>
    /// Child object every mesh of this renderer is parented to. Destroying it on Dispose takes the
    /// mesh objects with it, so subclasses never tear their own children down.
    /// </summary>
    protected GameObject pcObject;

    protected float currentPointSize;
    protected float currentPointEmission = 1f;
    protected float currentOpacity = 1f;

    /// <summary>True once Setup has built everything SetFrame needs.</summary>
    protected bool ready;
    protected bool isDisposed;

    //One entry for the single-mesh paths, one per meshlet for the meshlet path, so show/hide and
    //the material writes are the same loop either way.
    readonly List<MeshRenderer> pointRenderers = new List<MeshRenderer>();
    //Only meshes this renderer allocated itself. The meshlet path draws the prefab's mesh straight
    //out of Resources and registers nothing, so Dispose must never destroy it.
    readonly List<Mesh> ownedMeshes = new List<Mesh>();

    Material sourceMaterial;
    Material defaultMaterial;
    bool instantiateMaterial;
    bool visible = true;
    bool warnedAboutOpacity;
    //Renderers from this index on are switched off. Starts past any real count so the single-mesh
    //paths, which clamp inside their one mesh instead, never touch it.
    int drawnRendererCount = int.MaxValue;

    //Property IDs. Every pointcloud shader in the package spells these the same way, whether it is
    //hand-written HLSL, a compute shader or a Shadergraph asset - the graphs carry an explicit
    //override reference name to make that true, since their generated one differs in case.
    protected static readonly int pointScaleID = Shader.PropertyToID("_PointScale");
    protected static readonly int emissionID = Shader.PropertyToID("_Emission");
    protected static readonly int alphaID = Shader.PropertyToID("_Alpha");
    //Material-driven render state, the way URP's own Lit shader does it, so one shader covers both
    //the opaque and the faded case. The hand-written shaders declare _SrcBlend/_DstBlend themselves;
    //on the Shader Graph paths the same names come from the graph's "Allow Material Override".
    protected static readonly int srcBlendID = Shader.PropertyToID("_SrcBlend");
    protected static readonly int dstBlendID = Shader.PropertyToID("_DstBlend");
    protected static readonly int zWriteID = Shader.PropertyToID("_ZWrite");
    protected static readonly int pointSourceBufferID = Shader.PropertyToID("_PointSourceBuffer");
    protected static readonly int pointCountID = Shader.PropertyToID("_PointCount");
    protected static readonly int sourceStrideID = Shader.PropertyToID("_SourceStride");

    /// <summary>Name of the child GameObject the geometry is parented to.</summary>
    protected abstract string StreamObjectName { get; }

    /// <summary>
    /// True when every MeshRenderer needs a Material instance of its own even if the caller asked
    /// to share one. The meshlet path does: each meshlet needs its own _VertexIDOffset.
    /// </summary>
    protected virtual bool NeedsMaterialPerRenderer => false;

    /// <summary>
    /// Bytes per point in the interleaved source vertex buffer: position, optional normal, RGBA8
    /// color.
    /// </summary>
    protected int SourceByteStride => configuration != null && configuration.hasNormals ? 4 * 7 : 4 * 4;

    /// <summary>
    /// Why this renderer cannot play <paramref name="config"/>, or null if it can. Returning a
    /// reason logs it and aborts Setup before anything is allocated.
    /// </summary>
    protected virtual string GetUnsupportedReason(SequenceConfiguration config) => null;

    /// <summary>
    /// Build the render path's GPU resources and geometry. <see cref="pcObject"/> already exists
    /// and the appearance state is already stored, so any MeshRenderer handed to RegisterRenderer
    /// from here - or from a coroutine started here - comes up fully configured.
    /// </summary>
    protected abstract void OnSetup(SequenceConfiguration config);

    /// <summary>
    /// Release whatever the subclass allocated. Runs before pcObject is destroyed, and can run
    /// before any Setup did, so it has to tolerate null fields.
    /// </summary>
    protected virtual void ReleaseResources() { }

    /// <summary>
    /// Bind the render path's own inputs on a material about to be assigned to the renderer at
    /// <paramref name="rendererIndex"/>: the source render textures, the per-meshlet vertex offset.
    /// </summary>
    protected virtual void ConfigureMaterial(Material mat, int rendererIndex) { }

    /// <summary>
    /// The material used when the caller supplies none. Treated as a template - the base always
    /// instantiates it, so an implementation may hand back the Resources asset unchanged.
    /// </summary>
    protected abstract Material LoadDefaultMaterial();

    public void Setup(SequenceConfiguration configuration, Transform parent, PointcloudRenderSettings settings)
    {
      Dispose();

      string unsupported = GetUnsupportedReason(configuration);
      if (unsupported != null)
      {
        Debug.LogError(unsupported);
        return;
      }

      isDisposed = false;
      visible = true;
      this.configuration = configuration;
      this.instantiateMaterial = settings.instantiateMaterial;
      sourceMaterial = settings.material;
      //Stored before OnSetup so every renderer registered from it - or from a coroutine it starts -
      //picks the appearance up through RegisterRenderer instead of being patched afterwards.
      currentPointSize = settings.pointSize;
      currentPointEmission = settings.emission;
      currentOpacity = settings.opacity;

      pcObject = CreateStreamObject(StreamObjectName, parent);

      OnSetup(configuration);

      ready = true;
    }

    public abstract void SetFrame(Frame frame);

    /// <summary>
    /// Wait for the loader jobs that produced a frame's geometry. Completing a handle that was
    /// never scheduled is a no-op, so the decompression handle needs no guard on uncompressed
    /// sequences.
    /// </summary>
    protected static void CompleteFrameJobs(Frame frame)
    {
      frame.geoJobHandle.Complete();
      frame.decompressionJobHandle.Complete();
    }

    protected GameObject CreateStreamObject(string name, Transform parent)
    {
      GameObject newStreamObject = new GameObject(name);
      newStreamObject.transform.parent = parent;
      newStreamObject.transform.localPosition = Vector3.zero;
      newStreamObject.transform.localRotation = Quaternion.identity;
      newStreamObject.transform.localScale = Vector3.one;
      newStreamObject.hideFlags = HideFlags.DontSave;
      return newStreamObject;
    }

    /// <summary>
    /// Give the stream object the MeshFilter/MeshRenderer pair the single-mesh paths draw through,
    /// carrying a fresh mesh bounded by the sequence bounds. The mesh is registered as owned; the
    /// renderer deliberately is not, so the caller can finish building the geometry before the
    /// material gets bound to it.
    /// </summary>
    protected Mesh CreateStreamMesh(out MeshFilter meshFilter, out MeshRenderer meshRenderer)
    {
      meshFilter = pcObject.GetComponent<MeshFilter>();
      if (meshFilter == null)
        meshFilter = pcObject.AddComponent<MeshFilter>();

      meshRenderer = pcObject.GetComponent<MeshRenderer>();
      if (meshRenderer == null)
        meshRenderer = pcObject.AddComponent<MeshRenderer>();

      meshFilter.hideFlags = HideFlags.DontSave;
      meshRenderer.hideFlags = HideFlags.DontSave;

      Mesh mesh = new Mesh();
      mesh.bounds = configuration.GetBounds();
      meshFilter.sharedMesh = mesh;

      RegisterOwnedMesh(mesh);

      return mesh;
    }

    /// <summary>
    /// Take ownership of a MeshRenderer drawing part of this cloud: it is put into the current
    /// material, point size, emission, opacity and visibility state now, and follows every later
    /// change.
    /// </summary>
    protected void RegisterRenderer(MeshRenderer renderer)
    {
      if (renderer == null)
        return;

      pointRenderers.Add(renderer);
      ApplyMaterial(pointRenderers.Count - 1);
      renderer.enabled = ShouldDraw(pointRenderers.Count - 1);
    }

    /// <summary>How many MeshRenderers the cloud is currently split across.</summary>
    protected int RendererCount => pointRenderers.Count;

    /// <summary>
    /// Draw only the first <paramref name="count"/> registered renderers, for paths whose geometry
    /// is split across renderers that share one mesh and so cannot be clamped individually.
    /// </summary>
    /// <remarks>
    /// The surplus renderers are disabled rather than destroyed, because the point count varies per
    /// frame and a later frame needs them back. Renderers registered after this call - the meshlet
    /// path keeps adding them for several frames - come up obeying the count too.
    /// </remarks>
    protected void SetDrawnRendererCount(int count)
    {
      if (count == drawnRendererCount)
        return;

      drawnRendererCount = count;

      for (int i = 0; i < pointRenderers.Count; i++)
        if (pointRenderers[i] != null)
          pointRenderers[i].enabled = ShouldDraw(i);
    }

    bool ShouldDraw(int rendererIndex) => visible && rendererIndex < drawnRendererCount;

    /// <summary>
    /// Declare a mesh this renderer allocated, so Dispose destroys it. Meshes that come from a
    /// prefab or any other asset must not be registered.
    /// </summary>
    protected void RegisterOwnedMesh(Mesh mesh)
    {
      if (mesh != null)
        ownedMeshes.Add(mesh);
    }

    public void SetPointcloudMaterial(Material mat, bool instantiateMaterial)
    {
      SetPointcloudMaterial(mat, currentPointSize, currentPointEmission, instantiateMaterial);
    }

    public void SetPointcloudMaterial(Material mat, float pointSize, float pointEmission, bool instantiateMaterial)
    {
      if (isDisposed)
        return;

      sourceMaterial = mat;
      this.instantiateMaterial = instantiateMaterial;
      currentPointSize = pointSize;
      currentPointEmission = pointEmission;

      for (int i = 0; i < pointRenderers.Count; i++)
        ApplyMaterial(i);
    }

    void ApplyMaterial(int rendererIndex)
    {
      MeshRenderer renderer = pointRenderers[rendererIndex];
      if (renderer == null)
        return;

      Material source = sourceMaterial;
      bool isDefault = source == null;

      if (isDefault)
      {
        if (defaultMaterial == null)
          defaultMaterial = LoadDefaultMaterial();

        source = defaultMaterial;
      }

      if (source == null)
        return;

      //A default material is the shared Resources asset, so it is always copied first: the point
      //size and emission are written into the material, and two streams must not stomp one asset.
      Material applied = instantiateMaterial || isDefault || NeedsMaterialPerRenderer ? new Material(source) : source;

      ConfigureMaterial(applied, rendererIndex);
      renderer.sharedMaterial = applied;

      ApplyPointSize(rendererIndex);
      ApplyPointEmission(rendererIndex);
      ApplyOpacity(rendererIndex);
    }

    public virtual void SetPointSize(float size)
    {
      if (isDisposed)
        return;

      currentPointSize = size;

      for (int i = 0; i < pointRenderers.Count; i++)
        ApplyPointSize(i);
    }

    public virtual void SetPointEmission(float emission)
    {
      if (isDisposed)
        return;

      currentPointEmission = emission;

      for (int i = 0; i < pointRenderers.Count; i++)
        ApplyPointEmission(i);
    }

    public virtual void SetOpacity(float opacity)
    {
      if (isDisposed)
        return;

      currentOpacity = Mathf.Clamp01(opacity);

      for (int i = 0; i < RendererCount; i++)
        ApplyOpacity(i);
    }

    /// <summary>
    /// Push the current point size to one renderer. Overridden by paths that carry the point size
    /// somewhere other than the material.
    /// </summary>
    protected virtual void ApplyPointSize(int rendererIndex)
    {
      SetMaterialFloat(rendererIndex, pointScaleID, currentPointSize);
    }

    protected virtual void ApplyPointEmission(int rendererIndex)
    {
      SetMaterialFloat(rendererIndex, emissionID, currentPointEmission);
    }

    /// <summary>
    /// Push the current opacity to one renderer. The base only writes the uniform, which is all an
    /// alpha-clip graph needs; paths whose shader also carries the blend state override this to
    /// switch it with the mode.
    /// </summary>
    protected virtual void ApplyOpacity(int rendererIndex)
    {
      Material mat = GetMaterial(rendererIndex);

      if (mat == null)
        return;

      if (!SupportsOpacity(mat))
        return;

      mat.SetFloat(alphaID, currentOpacity);
    }

    /// <summary>
    /// Whether a material's shader can honour the opacity at all, warning once per renderer if it
    /// cannot and somebody has actually asked for one below 1.
    /// </summary>
    /// <remarks>
    /// Silence would be worse than a warning here: a render path or a user's own shader that has no
    /// _Alpha simply keeps drawing fully opaque, and an inspector slider that does nothing with no
    /// explanation is the sort of thing people debug for an hour.
    /// </remarks>
    protected bool SupportsOpacity(Material mat)
    {
      if (mat.HasFloat(alphaID))
        return true;

      if (currentOpacity < 1f && !warnedAboutOpacity)
      {
        warnedAboutOpacity = true;
        Debug.LogWarning("Shader '" + mat.shader.name + "' has no _Alpha property, so the sequence's opacity setting has no effect on it. Use one of the package's own pointcloud materials, or add an _Alpha property to your shader.", this);
      }

      return false;
    }

    /// <summary>
    /// The opacity implementation for a hand-written pointcloud shader that exposes _Alpha and
    /// material-driven blend factors. Restores the exact state the shader ships with at opacity 1,
    /// so full opacity costs nothing.
    /// </summary>
    /// <param name="opaqueQueue">The queue the shader declares in its own tags, to go back to when not blending.</param>
    /// <remarks>
    /// A Shader Graph path cannot use this: surface type is baked into the graph asset and there is
    /// no material-driven blend state to write. Those paths need a second graph, and until they
    /// have one they inherit the base implementation, which writes _Alpha and nothing else.
    /// </remarks>
    protected void ApplyMaterialDrivenOpacity(int rendererIndex, RenderQueue opaqueQueue)
    {
      Material mat = GetMaterial(rendererIndex);

      if (mat == null || !SupportsOpacity(mat))
        return;

      //Blending at full opacity would buy nothing, so it only engages once the opacity is actually
      //below 1 - which also keeps the shipped, fully opaque case byte-identical to what it was
      //before opacity existed.
      bool blended = currentOpacity < 1f;

      mat.SetFloat(alphaID, currentOpacity);

      mat.SetFloat(srcBlendID, (float)(blended ? BlendMode.SrcAlpha : BlendMode.One));
      mat.SetFloat(dstBlendID, (float)(blended ? BlendMode.OneMinusSrcAlpha : BlendMode.Zero));

      //Only the queue and the blend factors move. ZWrite is hard On in the shader even while
      //blending, which is not what a transparent shader normally does:
      //
      //A cloud is thousands of points at every depth, in whatever order the sequence file lists
      //them, and nothing sorts them. With ZWrite off, a point behind another one drawn later blends
      //over it at full strength - at opacity 0.95 the nearer point comes out 95% replaced by the one
      //behind it, so it reads as having vanished outright. Measured in the Editor, and it flips with
      //nothing but the order the two points sit in the vertex buffer. Keeping the depth write makes
      //the nearest point win its pixel exactly as it does when opaque; the cost is that a cloud's
      //own far side no longer shows through its near side, which is not something a pointcloud
      //conveys usefully anyway.
      mat.renderQueue = (int)(blended ? RenderQueue.Transparent : opaqueQueue);
    }

    void SetMaterialFloat(int rendererIndex, int propertyID, float value)
    {
      Material mat = GetMaterial(rendererIndex);

      if (mat != null && mat.HasFloat(propertyID))
        mat.SetFloat(propertyID, value);
    }

    /// <summary>The material one registered renderer is currently drawing with, or null.</summary>
    protected Material GetMaterial(int rendererIndex)
    {
      MeshRenderer renderer = pointRenderers[rendererIndex];
      return renderer != null ? renderer.sharedMaterial : null;
    }

    public void Show()
    {
      SetVisible(true);
    }

    public void Hide()
    {
      SetVisible(false);
    }

    void SetVisible(bool value)
    {
      //Remembered, not just pushed to the renderers that exist right now: the meshlet path keeps
      //adding renderers for several frames after Setup returns, and those have to come up hidden
      //if Hide() was called in the meantime.
      visible = value;

      for (int i = 0; i < pointRenderers.Count; i++)
        if (pointRenderers[i] != null)
          pointRenderers[i].enabled = ShouldDraw(i);
    }

    public virtual void Dispose()
    {
      ReleaseResources();

      for (int i = 0; i < ownedMeshes.Count; i++)
        if (ownedMeshes[i] != null)
          DestroyImmediate(ownedMeshes[i]);

      ownedMeshes.Clear();
      pointRenderers.Clear();

      //Takes the mesh objects parented under it along, so no subclass has to destroy its own
      //children. The meshes above are separate: destroying a GameObject does not destroy the mesh
      //its MeshFilter points at.
      if (pcObject != null)
        DestroyImmediate(pcObject);

      pcObject = null;
      ready = false;
      isDisposed = true;
    }

    public bool IsDisposed()
    {
      return isDisposed;
    }
  }
}
