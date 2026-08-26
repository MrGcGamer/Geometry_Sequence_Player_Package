using UnityEngine;
using System.IO;
using Unity.Jobs.LowLevel.Unsafe;
using System;
using System.Collections.Generic;
using UnityEditor;


#if UNITY_VISIONOS && INCLUDE_UNITY_POLYSPATIAL
using Unity.PolySpatial;
#endif
#if UNITY_EDITOR
using UnityEditor.SceneManagement;
#endif

namespace BuildingVolumes.Player
{
    public class GeometrySequenceStream : MonoBehaviour
    {
        public string pathToSequence { get; private set; }

        public Bounds drawBounds = new Bounds(Vector3.zero, new Vector3(3, 3, 3));

        //Buffering options
        public int bufferSize = 30;
        public bool useAllThreads = true;
        public int threadCount = 4;

        //Debug
        public bool attachFrameDebugger = false;
        public GSFrameDebugger frameDebugger = null;

        //Materials
        public Material customMaterial;
        public bool instantiateMaterial = true;
        public MaterialProperties materialSlots = MaterialProperties.Albedo;
        public List<string> customMaterialSlots;

        //Pointcloud rendering options
        public PointcloudRenderPath pointRenderPath = PointcloudRenderPath.PolySpatial;
        public float pointSize = 0.02f;
        public float pointEmission = 1f;
        // Initialised to 1 so a scene saved before opacity existed deserialises to exactly what it
        // used to look like: the field is absent from its YAML and takes this value.
        public float pointOpacity = 1f;

        // Sequences are authored around sequence.json's boundsCenter, not the origin, so content may sit
        // off this GameObject's pivot and rotate/scale around a point outside it. When enabled, renderers
        // are parented to a child shifted by -boundsCenter so content aligns with this transform's origin.
        // Disable to preserve the original authored offset in existing scenes.
        public bool centerContentOnOrigin = true;

        //Owned here rather than by the renderers, because it must be identical for the playback and
        //thumbnail renderers and outlives neither of them alone.
        Transform contentRoot;

        //Mesh and pointcloud rendering
        [HideInInspector]
        public BufferedGeometryReader bufferedReader;
        public IPointCloudRenderer pointcloudRenderer;
        public IMeshSequenceRenderer meshSequenceRenderer;

        //Thumbnail rendering
        BufferedGeometryReader thumbnailReader;
        IPointCloudRenderer thumbnailPCRenderer;
        IMeshSequenceRenderer thumbnailMeshRenderer;

        //Buffering
        public bool readerInitialized = false;
        public bool frameDropped = false;
        public int framesDroppedCounter = 0;
        public int lastFrameBufferIndex = 0;
        public float targetFrameTimeMs = 0;
        public float lastFrameTime = 0;
        public int lastFrameIndex;
        public float sequenceDeltaTime = 0;
        public float elapsedMsSinceSequenceStart = 0;
        public float smoothedFPS = 0f;

        //Performance tracking
        float sequenceStartTime = 0;
        float lastSequenceCompletionTime;

        public enum PathType { AbsolutePath, RelativeToDataPath, RelativeToPersistentDataPath, RelativeToStreamingAssets };
        public enum PointcloudRenderPath { Shadergraph, Legacy, PolySpatial, Points };
        [Flags] public enum MaterialProperties { Albedo = 1, Emission = 2, Detail = 4 }

        private void Awake()
        {
#if !UNITY_EDITOR && !UNITY_STANDALONE_WIN && !UNITY_STANDALONE_OSX && !UNITY_STANDALONE_LINUX && !UNITY_IOS && !UNITY_ANDROID && !UNITY_TVOS && !UNITY_VISIONOS
            Debug.LogError("Platform not supported by Geometry Sequence Streamer! Playback will probably fail");
#endif
            if (!useAllThreads)
                JobsUtility.JobWorkerCount = threadCount;
            else
                JobsUtility.JobWorkerCount = JobsUtility.JobWorkerMaximumCount;

            if (attachFrameDebugger)
                AttachFrameDebugger();
        }

        /// <summary>
        /// Cleans up the current sequence and prepares the playback of the sequence in the given folder. Doesn't start playback!
        /// </summary>
        /// <param name="absolutePathToSequence">The absolute path to the folder containing a sequence of .ply (or, for Draco sequences, .drc) geometry files and optionally .dds texture files</param>
        public bool ChangeSequence(string absolutePathToSequence, float playbackFPS)
        {
            Dispose();
            lastFrameIndex = -1;
            lastFrameBufferIndex = -1;

            pathToSequence = absolutePathToSequence;
            bufferedReader = new BufferedGeometryReader();
            if (!bufferedReader.SetupReader(pathToSequence, bufferSize))
                return readerInitialized;

            //On Polyspatial, we force the render path to a special variant for this system
#if UNITY_VISIONOS && INCLUDE_UNITY_POLYSPATIAL
      UnityEngine.Object volumeCam = UnityEngine.Object.FindFirstObjectByType<Unity.PolySpatial.VolumeCamera>();
      if (volumeCam != null)
        pointRenderPath = PointcloudRenderPath.PolySpatial;
#endif

            if (bufferedReader.sequenceConfig.geometryType == SequenceConfiguration.GeometryType.Point)
                pointcloudRenderer = SetupPointcloudRenderer(bufferedReader, pointRenderPath);
            else
                meshSequenceRenderer = SetupMeshSequenceRenderer(bufferedReader, pointRenderPath);

            this.SetPlaybackFPS(playbackFPS);

            readerInitialized = true;
            return readerInitialized;
        }


        public void UpdateFrame()
        {
            if (!readerInitialized)
                return;

            if (bufferedReader.totalFrames == 1)
            {
                Frame singleFrame = bufferedReader.frameBuffer[0];

#if DRACO_AVAILABLE
                //Draco decodes asynchronously and cannot be completed synchronously here (the
                //continuation needs the main thread, so blocking would deadlock). Schedule once,
                //then show as soon as the decode is ready on a later UpdateFrame call.
                if (bufferedReader.sequenceConfig.compressionMethod == SequenceConfiguration.CompressionMethod.Draco)
                {
                    if (singleFrame.bufferState == BufferState.Empty)
                        bufferedReader.ScheduleFrame(singleFrame, 0);
                    if (singleFrame.dracoReady && singleFrame.bufferState != BufferState.Playing)
                    {
                        ShowFrame(singleFrame);
                        singleFrame.bufferState = BufferState.Playing;
                    }
                    return;
                }
#endif

                bufferedReader.SetupFrameForReading(singleFrame, bufferedReader.sequenceConfig, 0);
                bufferedReader.ScheduleGeometryReadJob(singleFrame, bufferedReader.inputFilePaths[0]);
                singleFrame.geoJobHandle.Complete();
                ShowFrame(singleFrame);
                return;
            }


            sequenceDeltaTime += Time.deltaTime * 1000;
            elapsedMsSinceSequenceStart += Time.deltaTime * 1000;
            if (elapsedMsSinceSequenceStart > targetFrameTimeMs * bufferedReader.totalFrames) //If we wrap around our ring buffer
            {
                elapsedMsSinceSequenceStart -= targetFrameTimeMs * bufferedReader.totalFrames;

                //For performance tracking
                lastSequenceCompletionTime = (Time.time - sequenceStartTime) * lastSequenceCompletionTime;
                sequenceStartTime = Time.time;
                framesDroppedCounter = 0;
            }

            int targetFrameIndex = Mathf.RoundToInt(elapsedMsSinceSequenceStart / targetFrameTimeMs);

            //Check how many frames our targetframe is in advance relative to the last played frame
            int framesInAdvance = 0;
            if (targetFrameIndex > lastFrameIndex)
                framesInAdvance = targetFrameIndex - lastFrameIndex;

            if (targetFrameIndex < lastFrameIndex)
                framesInAdvance = (bufferedReader.totalFrames - lastFrameIndex) + targetFrameIndex;

            frameDropped = framesInAdvance > 1 ? true : false;
            if (frameDropped)
                framesDroppedCounter += framesInAdvance - 1;

            //Debug.Log("Elapsed MS in sequence: " + elapsedMsSinceSequenceStart + ", target Index: " + targetFrameIndex + ", last frame: " + lastFrameIndex + ", target in advance: " + targetFrameIndex);

            bufferedReader.BufferFrames(targetFrameIndex, lastFrameIndex);

            if (framesInAdvance > 0)
            {
                //Check if our desired frame is inside the frame buffer and loaded, so that we can use it
                int newBufferIndex = bufferedReader.GetBufferIndexForLoadedPlaybackIndex(targetFrameIndex);

                //Is the frame inside the buffer and fully loaded?
                if (newBufferIndex > -1)
                {
                    //Now that we a show a new frame, we mark the old played frame as consumed, and the new frame as playing
                    if (lastFrameBufferIndex > -1)
                        bufferedReader.frameBuffer[lastFrameBufferIndex].bufferState = BufferState.Consumed;

                    bufferedReader.frameBuffer[newBufferIndex].bufferState = BufferState.Playing;
                    ShowFrame(bufferedReader.frameBuffer[newBufferIndex]);
                    lastFrameBufferIndex = newBufferIndex;
                    lastFrameIndex = targetFrameIndex;

                    //Sometimes, the system might struggle to render a frame, or the application has a low framerate in general
                    //For performance tracking, we need to decouple the application framerate from our stream framerate.
                    //If we are lagging behind due to render reasons, but have sucessfully buffered up to the current target frame
                    //we still hit our target time window and the stream is performing well
                    //Therefore we substract the dropped frames from our deltatime

                    if (frameDropped && framesInAdvance > 1)
                        sequenceDeltaTime -= (framesInAdvance - 1) * targetFrameTimeMs;

                    float decay = 0.9f;
                    smoothedFPS = decay * smoothedFPS + (1.0f - decay) * (1000f / sequenceDeltaTime);

                    lastFrameTime = sequenceDeltaTime;
                    sequenceDeltaTime = 0;
                }
            }

            if (frameDebugger != null)
            {
                frameDebugger.UpdateFrameDebugger(this);
            }

            //TODO: Buffering callback
        }

        public void SetFrameTime(float frameTimeMS)
        {
            elapsedMsSinceSequenceStart = frameTimeMS;
            lastFrameIndex = (int)(frameTimeMS / targetFrameTimeMs);
            UpdateFrame();
        }

        public void SetPlaybackFPS(float rate)
        {
            targetFrameTimeMs = 1000f / (float)rate;
            smoothedFPS = rate;
        }

        /// <summary>
        /// Render the frame
        /// </summary>
        /// <param name="frame"></param>
        public void ShowFrame(Frame frame)
        {
            if (bufferedReader.sequenceConfig.geometryType == SequenceConfiguration.GeometryType.Point)
                pointcloudRenderer?.SetFrame(frame);
            else
                meshSequenceRenderer?.RenderFrame(frame);

            frame.finishedBufferingTime = 0;
        }

        /// <summary>
        /// The transform every renderer parents its stream object to. Carries the offset that puts the
        /// sequence's content on this GameObject's origin, so no render path has to know about it.
        /// </summary>
        Transform GetContentRoot(SequenceConfiguration config)
        {
            if (contentRoot == null)
            {
                GameObject rootObject = new GameObject("Geometry Sequence Content");
                rootObject.hideFlags = HideFlags.DontSave;
                rootObject.transform.parent = this.transform;
                rootObject.transform.localRotation = Quaternion.identity;
                rootObject.transform.localScale = Vector3.one;
                contentRoot = rootObject.transform;
            }

            contentRoot.localPosition = ContentOffset(config);

            return contentRoot;
        }

        //Position only: the legacy path builds its camera-facing quads from the renderer's own
        //transform.rotation, so a rotated or scaled content root would silently break it.
        Vector3 ContentOffset(SequenceConfiguration config)
        {
            return centerContentOnOrigin ? -config.GetBounds().center : Vector3.zero;
        }

        /// <summary>
        /// Re-applies centerContentOnOrigin to a content root that already exists, so toggling the flag
        /// moves the visible sequence straight away instead of waiting for the next ChangeSequence.
        /// </summary>
        public void RefreshContentPlacement()
        {
            SequenceConfiguration config = bufferedReader?.sequenceConfig ?? thumbnailReader?.sequenceConfig;

            if (contentRoot == null || config == null)
                return;

            contentRoot.localPosition = ContentOffset(config);
        }

        /// <summary>
        /// Destroys the content root once the last renderer has torn its stream object down. Playback and
        /// thumbnail renderers share the root and are disposed by different paths, so ownership is decided
        /// by what is left under it rather than by which caller ran.
        /// </summary>
        void ReleaseContentRootIfEmpty()
        {
            if (contentRoot == null || contentRoot.childCount > 0)
                return;

            DestroyImmediate(contentRoot.gameObject);
            contentRoot = null;
        }

        public IPointCloudRenderer SetupPointcloudRenderer(BufferedGeometryReader reader, PointcloudRenderPath renderPath)
        {
            IPointCloudRenderer pcRenderer;

#if !SHADERGRAPH_AVAILABLE
            //Only the Shadergraph/PolySpatial paths require Shadergraph; Legacy doesn't.
            if (renderPath == PointcloudRenderPath.Shadergraph || renderPath == PointcloudRenderPath.PolySpatial)
            {
                Debug.LogWarning("Shadergraph package not available, falling back to legacy pointcloud sequence rendering");
                renderPath = PointcloudRenderPath.Legacy;
            }
#endif

            switch (renderPath)
            {
                case PointcloudRenderPath.Shadergraph:
                    pcRenderer = gameObject.AddComponent<PointcloudRendererRT>();
                    break;
                case PointcloudRenderPath.Legacy:
                    pcRenderer = gameObject.AddComponent<PointcloudRenderer>();
                    break;
                case PointcloudRenderPath.PolySpatial:
                    pcRenderer = gameObject.AddComponent<PointcloudRendererRT_Meshlet>();
                    break;
                case PointcloudRenderPath.Points:
                    pcRenderer = gameObject.AddComponent<PointcloudRendererPoints>();
                    break;
                default:
                    pcRenderer = gameObject.AddComponent<PointcloudRendererRT>();
                    break;
            }

            (pcRenderer as Component).hideFlags = HideFlags.DontSave;
            pcRenderer.Setup(reader.sequenceConfig, GetContentRoot(reader.sequenceConfig), GetRenderSettings());

            return pcRenderer;
        }

        public IMeshSequenceRenderer SetupMeshSequenceRenderer(BufferedGeometryReader reader, PointcloudRenderPath renderPath)
        {
            IMeshSequenceRenderer msRenderer;

#if !SHADERGRAPH_AVAILABLE
            if (renderPath != PointcloudRenderPath.Legacy)
            {
                Debug.LogWarning("Shadergraph package not available, falling back to legacy mesh sequence rendering");
                renderPath = PointcloudRenderPath.Legacy;
            }
#endif

            if (renderPath == PointcloudRenderPath.PolySpatial)
                msRenderer = gameObject.AddComponent<MeshSequenceRendererSC>();
            else
                msRenderer = gameObject.AddComponent<MeshSequenceRenderer>();

            (msRenderer as Component).hideFlags = HideFlags.HideAndDontSave;
            msRenderer.Setup(GetContentRoot(reader.sequenceConfig), reader.sequenceConfig);

            if (customMaterial != null)
                msRenderer.ChangeMaterial(customMaterial, instantiateMaterial);

            //If we have a single texture in the sequence, we read it immeiatly
            if (reader.sequenceConfig.textureMode == SequenceConfiguration.TextureMode.Single)
            {
                reader.SetupFrameForReading(reader.frameBuffer[0], reader.sequenceConfig, 0);
                reader.ScheduleTextureReadJob(reader.frameBuffer[0], reader.GetDeviceDependentTexturePath(0));
                reader.frameBuffer[0].textureJobHandle.Complete();
                msRenderer.ApplySingleTexture(reader.frameBuffer[0]);
            }

            return msRenderer;
        }

        public void SetPointSize(float pointSize)
        {
            pointcloudRenderer?.SetPointSize(pointSize);
            thumbnailPCRenderer?.SetPointSize(pointSize);
        }

        public void SetPointEmission(float pointEmission)
        {
            pointcloudRenderer?.SetPointEmission(pointEmission);
            thumbnailPCRenderer?.SetPointEmission(pointEmission);
        }

        /// <summary>
        /// Sets a uniform opacity over the whole sequence, pointcloud or mesh. This is a global
        /// multiplier, not per-point alpha from the sequence data - the source colours carry no
        /// usable alpha channel.
        /// </summary>
        /// <param name="opacity">0 (invisible) to 1 (as before). Values outside are clamped.</param>
        public void SetOpacity(float opacity)
        {
            pointOpacity = Mathf.Clamp01(opacity);

            pointcloudRenderer?.SetOpacity(pointOpacity);
            thumbnailPCRenderer?.SetOpacity(pointOpacity);

            meshSequenceRenderer?.SetOpacity(pointOpacity);
            thumbnailMeshRenderer?.SetOpacity(pointOpacity);
        }

        /// <summary>
        /// The appearance state a renderer should be built in. Read at Setup time only; later edits
        /// go through the SetX methods above so both the playback and the thumbnail renderer follow.
        /// </summary>
        PointcloudRenderSettings GetRenderSettings()
        {
            return new PointcloudRenderSettings
            {
                pointSize = pointSize,
                emission = pointEmission,
                opacity = Mathf.Clamp01(pointOpacity),
                material = customMaterial,
                instantiateMaterial = instantiateMaterial
            };
        }


        public void SetMaterial(Material mat)
        {
            SetMaterial(mat, instantiateMaterial);
        }

        public void SetMaterial(Material mat, bool instantiate)
        {
            customMaterial = mat;
            pointcloudRenderer?.SetPointcloudMaterial(mat, instantiate);
            thumbnailPCRenderer?.SetPointcloudMaterial(mat, instantiate);

            meshSequenceRenderer?.ChangeMaterial(mat, instantiate);
            thumbnailMeshRenderer?.ChangeMaterial(mat, instantiate);
        }

        /// <summary>
        /// Switches render path, rebuilding the renderer in place when a sequence is already open.
        /// </summary>
        /// <remarks>
        /// The reader, its ring buffer and the playback clock are all left alone: only the renderer
        /// is torn down and replaced, so switching costs one frame of setup rather than a reopen and
        /// a rebuffer. Called before <see cref="ChangeSequence"/>, it just records the choice.
        ///
        /// A path is never validated here - <see cref="SetupPointcloudRenderer"/> already falls back
        /// where the packages a path needs are absent, and this is the only place that knows.
        /// </remarks>
        public void SetRenderingPath(PointcloudRenderPath renderPath)
        {
            bool changed = renderPath != pointRenderPath;
            pointRenderPath = renderPath;

            if (!changed || !readerInitialized || bufferedReader == null)
                return;

            RebuildRenderer();
        }

        /// <summary>
        /// Replaces the live renderer with one built for the current <see cref="pointRenderPath"/>.
        /// </summary>
        /// <remarks>
        /// The old component is destroyed rather than merely disposed. Every render path adds its
        /// renderer to this same GameObject, so leaving the previous one attached would keep a second
        /// disposed component running its own Update alongside the new one.
        ///
        /// DestroyImmediate, not Destroy: a deferred destroy leaves the outgoing component alive for
        /// the rest of the frame, and both would be given the frame pushed below.
        /// </remarks>
        void RebuildRenderer()
        {
            bool isPointcloud = bufferedReader.sequenceConfig.geometryType == SequenceConfiguration.GeometryType.Point;

            if (isPointcloud)
            {
                pointcloudRenderer?.Dispose();
                DestroyRenderer(pointcloudRenderer as Component);
                pointcloudRenderer = SetupPointcloudRenderer(bufferedReader, pointRenderPath);
            }

            else
            {
                meshSequenceRenderer?.Dispose();
                DestroyRenderer(meshSequenceRenderer as Component);
                meshSequenceRenderer = SetupMeshSequenceRenderer(bufferedReader, pointRenderPath);
            }

            //The pointcloud path takes its appearance from GetRenderSettings at Setup; the mesh path
            //has no equivalent, so opacity is pushed back in for both here.
            SetOpacity(pointOpacity);

            ShowCurrentFrameAgain();
        }

        static void DestroyRenderer(Component renderer)
        {
            if (renderer != null)
                DestroyImmediate(renderer);
        }

        /// <summary>
        /// Pushes the frame already on screen into a freshly built renderer, so a mid-playback swap
        /// does not blank the sequence until the clock reaches the next frame.
        /// </summary>
        /// <remarks>
        /// Only a frame in <see cref="BufferState.Playing"/> qualifies: any other state means its
        /// buffers are still being written into. The single-frame path needs no help - it re-reads
        /// and re-shows its one frame on every <see cref="UpdateFrame"/> anyway.
        /// </remarks>
        void ShowCurrentFrameAgain()
        {
            if (lastFrameBufferIndex < 0 || bufferedReader.frameBuffer == null)
                return;

            Frame current = bufferedReader.frameBuffer[lastFrameBufferIndex];
            if (current != null && current.bufferState == BufferState.Playing)
                ShowFrame(current);
        }

        public void ShowSequence()
        {
            pointcloudRenderer?.Show();
            meshSequenceRenderer?.Show();
        }

        public void HideSequence()
        {
            pointcloudRenderer?.Hide();
            meshSequenceRenderer?.Hide();
        }

        public void Dispose()
        {
#if UNITY_EDITOR && DRACO_AVAILABLE
            StopDracoThumbnailPoll();
#endif

            pointcloudRenderer?.Dispose();
            meshSequenceRenderer?.Dispose();

            thumbnailPCRenderer?.Dispose();
            meshSequenceRenderer?.Dispose();

            bufferedReader?.DisposeFrameBuffer(true);
            readerInitialized = false;

            ReleaseContentRootIfEmpty();
        }

        [ExecuteInEditMode]
        void OnDestroy()
        {
#if UNITY_EDITOR
      if (!Application.isPlaying)
      {
        ClearEditorThumbnail();
        return;
      }
#endif
            Dispose();
        }

        #region Thumbnail

#if UNITY_EDITOR

    /// <summary>
    /// Loads and shows a thumbnail of the clip that was just opened. Only shown in the editor
    /// </summary>
    /// <param name="pathToSequence"></param>
    public void LoadEditorThumbnail(string pathToSequence)
    {
      if (BuildPipeline.isBuildingPlayer)
        return;

      ClearEditorThumbnail();

      if (Directory.Exists(pathToSequence))
      {
        thumbnailReader = new BufferedGeometryReader();
        if (!thumbnailReader.SetupReader(pathToSequence, 1))
        {
          Debug.LogWarning("Could not load thumbnail for sequence: " + pathToSequence);
          return;
        }

        Frame thumbnail = thumbnailReader.frameBuffer[0];

#if DRACO_AVAILABLE
        bool waitingForDracoDecode = false;

        //Draco decodes off the main thread, so the frame holds no points yet when the renderer is set up
        //below. Kick off the decode and push the frame in from the editor update loop once it lands.
        if (thumbnailReader.sequenceConfig.compressionMethod == SequenceConfiguration.CompressionMethod.Draco)
        {
          thumbnailReader.ScheduleFrame(thumbnail, 0);
          waitingForDracoDecode = true;
        }
        else
#endif
        {
          thumbnailReader.SetupFrameForReading(thumbnail, thumbnailReader.sequenceConfig, 0);
          thumbnailReader.ScheduleGeometryReadJob(thumbnail, thumbnailReader.inputFilePaths[0]);
        }

        if (thumbnailReader.sequenceConfig.geometryType == SequenceConfiguration.GeometryType.Point)
        {
          thumbnailPCRenderer = SetupPointcloudRenderer(thumbnailReader, pointRenderPath);

#if DRACO_AVAILABLE
          if (waitingForDracoDecode)
            PollForDracoThumbnail(thumbnail);
          else
#endif
            thumbnailPCRenderer?.SetFrame(thumbnail);
        }

        else
        {
          thumbnailMeshRenderer = SetupMeshSequenceRenderer(thumbnailReader, pointRenderPath);

          if (thumbnailReader.sequenceConfig.textureMode != SequenceConfiguration.TextureMode.None)
          {
            thumbnailReader.ScheduleTextureReadJob(thumbnail, thumbnailReader.GetDeviceDependentTexturePath(0));
          }

          thumbnailMeshRenderer?.RenderFrame(thumbnailReader.frameBuffer[0]);
        }
      }
    }

#if DRACO_AVAILABLE
    EditorApplication.CallbackFunction dracoThumbnailPoll;

    /// <summary>
    /// Shows the thumbnail frame once its Draco decode has finished. The decode is a Task, so it can't
    /// be waited on here without deadlocking the main thread - we poll the frame from the editor's
    /// update loop instead, the same way the runtime buffer state machine polls dracoReady.
    /// </summary>
    void PollForDracoThumbnail(Frame thumbnail)
    {
      StopDracoThumbnailPoll();

      dracoThumbnailPoll = () =>
      {
        //Cleared, or the scene closed, while the decode was still running
        if (thumbnailReader == null || thumbnailReader.frameBuffer == null)
        {
          StopDracoThumbnailPoll();
          return;
        }

        if (!thumbnail.dracoReady)
          return;

        StopDracoThumbnailPoll();

        thumbnailPCRenderer?.SetFrame(thumbnail);

        //Nothing else marks the scene dirty at this point, so without this the thumbnail would only
        //appear once the user next moves the mouse over a scene view.
        SceneView.RepaintAll();
      };

      EditorApplication.update += dracoThumbnailPoll;
    }

    void StopDracoThumbnailPoll()
    {
      if (dracoThumbnailPoll == null)
        return;

      EditorApplication.update -= dracoThumbnailPoll;
      dracoThumbnailPoll = null;
    }
#endif

    /// <summary>
    /// Removes the shown Thumbnail, so that it doesn't stick around in the scene or get saved
    /// </summary>
    public void ClearEditorThumbnail()
    {
#if DRACO_AVAILABLE
      //Must run before the buffers are disposed: the callback reads the frame we are about to free
      StopDracoThumbnailPoll();
#endif

      thumbnailReader?.DisposeFrameBuffer(true);
      thumbnailMeshRenderer?.Dispose();
      thumbnailPCRenderer?.Dispose();

      if (thumbnailMeshRenderer != null)
        DestroyImmediate(thumbnailMeshRenderer as UnityEngine.Object);
      if (thumbnailPCRenderer != null)
        DestroyImmediate(thumbnailPCRenderer as UnityEngine.Object);

      ReleaseContentRootIfEmpty();
    }
#endif
        #endregion

        #region Debug
        void AttachFrameDebugger()
        {
#if UNITY_EDITOR
      GameObject debugGO = Resources.Load("GSFrameDebugger") as GameObject;
      frameDebugger = Instantiate(debugGO).GetComponent<GSFrameDebugger>();
      frameDebugger.GetCanvas().renderMode = RenderMode.ScreenSpaceOverlay;
#endif
        }
        #endregion
    }
}
