using System.Collections.Generic;
using UnityEngine;

namespace BuildingVolumes.Player
{
  /// <summary>
  /// Shared base for all pointcloud renderers. It owns everything that does not depend on how the
  /// points reach the GPU: the setup/teardown lifecycle, the child GameObject the geometry is
  /// parented to, the set of MeshRenderers drawing the cloud, and the material, point size and
  /// emission bookkeeping.
  ///
  /// A subclass builds its geometry and GPU resources in <see cref="OnSetup"/>, hands every
  /// MeshRenderer it creates to <see cref="RegisterRenderer"/>, and frees what it allocated in
  /// <see cref="ReleaseResources"/>. Registration is what applies the current material, point size,
  /// emission and visibility, so renderers created after Setup returns (the meshlet path builds
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

    //Property IDs. Every pointcloud shader in the package spells these the same way, whether it is
    //hand-written HLSL, a compute shader or a Shadergraph asset - the graphs carry an explicit
    //override reference name to make that true, since their generated one differs in case.
    protected static readonly int pointScaleID = Shader.PropertyToID("_PointScale");
    protected static readonly int emissionID = Shader.PropertyToID("_Emission");
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
    /// and the material state is already stored, so any MeshRenderer handed to RegisterRenderer
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

    public void Setup(SequenceConfiguration configuration, Transform parent, float pointSize, float emission, Material mat, bool instantiateMaterial)
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
      this.instantiateMaterial = instantiateMaterial;
      sourceMaterial = mat;
      currentPointSize = pointSize;
      currentPointEmission = emission;

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
    /// material, point size, emission and visibility state now, and follows every later change.
    /// </summary>
    protected void RegisterRenderer(MeshRenderer renderer)
    {
      if (renderer == null)
        return;

      pointRenderers.Add(renderer);
      ApplyMaterial(pointRenderers.Count - 1);
      renderer.enabled = visible;
    }

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

    void SetMaterialFloat(int rendererIndex, int propertyID, float value)
    {
      MeshRenderer renderer = pointRenderers[rendererIndex];
      Material mat = renderer != null ? renderer.sharedMaterial : null;

      if (mat != null && mat.HasFloat(propertyID))
        mat.SetFloat(propertyID, value);
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
          pointRenderers[i].enabled = value;
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
