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
        public enum PointcloudRenderPath { Shadergraph, Legacy, PolySpatial, ShadergraphMeshlet };
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
        /// <param name="absolutePathToSequence">The absolute path to the folder containing a sequence of .ply geometry files and optionally .dds texture files</param>
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
                    if (singleFrame.dracoReady)
                        ShowFrame(singleFrame);
                    return;
                }
#endif

                bufferedReader.SetupFrameForReading(singleFrame, bufferedReader.sequenceConfig, 0);
                bufferedReader.ScheduleGeometryReadJob(singleFrame, bufferedReader.GetDeviceDependentTexturePath(0));
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

        public IPointCloudRenderer SetupPointcloudRenderer(BufferedGeometryReader reader, PointcloudRenderPath renderPath)
        {
            IPointCloudRenderer pcRenderer;

#if !SHADERGRAPH_AVAILABLE
            //Only the Shadergraph/PolySpatial paths require Shadergraph; Legacy doesn't.
            if (renderPath == PointcloudRenderPath.Shadergraph || renderPath == PointcloudRenderPath.PolySpatial || renderPath == PointcloudRenderPath.ShadergraphMeshlet)
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
                case PointcloudRenderPath.ShadergraphMeshlet:
                    pcRenderer = gameObject.AddComponent<PointcloudRendererRT_MeshletSG>();
                    break;
                default:
                    pcRenderer = gameObject.AddComponent<PointcloudRendererRT>();
                    break;
            }

            (pcRenderer as Component).hideFlags = HideFlags.DontSave;
            pcRenderer.Setup(reader.sequenceConfig, this.transform, pointSize, pointEmission, customMaterial, instantiateMaterial);

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
            msRenderer.Setup(this.transform, reader.sequenceConfig);

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

        public void SetRenderingPath(PointcloudRenderPath renderPath)
        {
            pointRenderPath = renderPath;
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
            pointcloudRenderer?.Dispose();
            meshSequenceRenderer?.Dispose();

            thumbnailPCRenderer?.Dispose();
            meshSequenceRenderer?.Dispose();

            bufferedReader?.DisposeFrameBuffer(true);
            readerInitialized = false;
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
        //Draco decodes asynchronously, so the thumbnail can't be produced synchronously here.
        //Kick off the decode; the frame simply won't render a thumbnail this pass (no crash).
        if (thumbnailReader.sequenceConfig.compressionMethod == SequenceConfiguration.CompressionMethod.Draco)
          thumbnailReader.ScheduleFrame(thumbnail, 0);
        else
#endif
        {
          thumbnailReader.SetupFrameForReading(thumbnail, thumbnailReader.sequenceConfig, 0);
          thumbnailReader.ScheduleGeometryReadJob(thumbnail, thumbnailReader.inputFilePaths[0]);
        }

        if (thumbnailReader.sequenceConfig.geometryType == SequenceConfiguration.GeometryType.Point)
        {
          thumbnailPCRenderer = SetupPointcloudRenderer(thumbnailReader, pointRenderPath);
          thumbnailPCRenderer?.SetFrame(thumbnailReader.frameBuffer[0]);
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

    /// <summary>
    /// Removes the shown Thumbnail, so that it doesn't stick around in the scene or get saved
    /// </summary>
    public void ClearEditorThumbnail()
    {
      thumbnailReader?.DisposeFrameBuffer(true);
      thumbnailMeshRenderer?.Dispose();
      thumbnailPCRenderer?.Dispose();

      if (thumbnailMeshRenderer != null)
        DestroyImmediate(thumbnailMeshRenderer as UnityEngine.Object);
      if (thumbnailPCRenderer != null)
        DestroyImmediate(thumbnailPCRenderer as UnityEngine.Object);
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
