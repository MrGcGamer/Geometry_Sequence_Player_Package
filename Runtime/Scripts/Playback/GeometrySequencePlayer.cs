using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Events;

namespace BuildingVolumes.Player
{
    public class GeometrySequencePlayer : MonoBehaviour
    {
        GeometrySequenceStream stream;

        [SerializeField]
        string relativePath = "";
        [SerializeField]
        GeometrySequenceStream.PathType pathRelation;

        [SerializeField]
        bool playAtStart = true;
        [SerializeField]
        bool loopPlay = true;
        [SerializeField]
        float playbackFPS = 30;

        public UnityEvent<GeometrySequencePlayer, GSPlayerEvents> playbackEvents = new UnityEvent<GeometrySequencePlayer, GSPlayerEvents>();

        float playbackTimeMs = 0;
        bool play = false;
        bool bufferingSequence = true;

        public enum GSPlayerEvents { Stopped, Started, Paused, SequenceChanged, BufferingStarted, BufferingCompleted, PlaybackStarted, PlaybackFinished, Looped, FrameDropped }

        // Start is called before the first frame update
        void Start()
        {
            SetupGeometryStream();

            if (playAtStart)
                if (!OpenSequence(relativePath, pathRelation, playbackFPS, playAtStart))
                    Debug.LogError("Could not open sequence! Please check the path and files!");
        }


        /// <summary>
        /// Add a Geometry Sequence Stream if there is non already existing on this gameobject.
        /// Automatically performed when adding this component in the editor
        /// </summary>
        public void SetupGeometryStream()
        {
            if (stream == null)
            {
                stream = gameObject.GetComponent<GeometrySequenceStream>();
                if (stream == null)
                    stream = gameObject.AddComponent<GeometrySequenceStream>();
            }
        }

        /// <summary>
        /// The main update loop. Only executes in the runtime/playmode
        /// </summary>
        private void Update()
        {
            if (play)
            {
                playbackTimeMs += Time.deltaTime * 1000;

                if (GetCurrentTime() >= GetTotalTime())
                {
                    if (!loopPlay)
                    {
                        Stop();
                        playbackEvents.Invoke(this, GSPlayerEvents.PlaybackFinished);
                    }

                    else
                    {
                        GoToTime(0);
                        playbackEvents.Invoke(this, GSPlayerEvents.Looped);
                    }
                }

                stream.UpdateFrame();

                if (GetFrameDropped())
                    playbackEvents.Invoke(this, GSPlayerEvents.FrameDropped);
            }
        }

        //+++++++++++++++++++++ PLAYBACK API ++++++++++++++++++++++++

        /// <summary>
        /// Load a .ply or Draco (.drc) sequence (and optionally textures) from the given path, and starts playback if autoplay is enabled.
        /// Returns false when sequence could not be loaded, see Unity Console output for details in this case.
        /// </summary>
        /// <param name="path">The path to the sequence</param>
        /// <param name="relativeTo">Which location is the path relative to?</param>
        /// <param name="playbackFPS">Desired playback framerate (Default = 30 fps)</param>
        /// <param name="autoplay">Start the playback as soon as possible. Might have a small delay as a frames need to load first</param>
        /// <param name="buffer">Preload the first few frames into memory before playing. Only applicable if autostart set to false. <</param>
        /// <returns>True when the sequence could sucessfully be loaded, false if an error has occured</returns>
        public bool OpenSequence(string path, GeometrySequenceStream.PathType relativeTo, float playbackFPS = 30f, bool autoplay = false, bool buffer = true)
        {
            if (path.Length > 0)
            {
                this.playbackFPS = playbackFPS;
                SetPath(path, relativeTo);
                return LoadCurrentSequence(autoplay, buffer);
            }

            return false;
        }

        /// <summary>
        /// (Re)loads the sequence which is currently set in the player, optionally starts playback.
        /// </summary>
        /// <param name="autoplay">Start playback immediatly after loading</param>
        /// <param name="buffer">Preload the first few frames into memory before playing. Only applicable if autostart set to false. <</param>
        /// <returns>True when the sequence could sucessfully be reloaded, false if an error has occured</returns>
        public bool LoadCurrentSequence(bool autoplay = false, bool buffer = true)
        {
            if (GetRelativeSequencePath().Length == 0)
                return false;

            playbackEvents.Invoke(this, GSPlayerEvents.SequenceChanged);

            bool sucess = stream.ChangeSequence(GetAbsoluteSequencePath(), playbackFPS);

            if (sucess)
            {
                if (buffer || autoplay)
                {
                    playbackEvents.Invoke(this, GSPlayerEvents.BufferingStarted);
                    stream.bufferedReader.BufferFrames(0, 0);
                    StartCoroutine(WaitForBufferingCompleted(autoplay));
                }
            }

            return sucess;
        }


        /// <summary>
        /// Set a new path in the player, but don't load the sequence. Use LoadCurrentSequence() to actually load it, or OpenSequence() to set and load a sequence.
        /// </summary>
        /// <param name="path">The relative or absolute path to the new Sequence</param>
        /// <param name="relativeTo">Specifiy to which path your sequence path is relative, or if it is an absolute path</param>
        public void SetPath(string path, GeometrySequenceStream.PathType relativeTo)
        {
            if (path.Length < 1)
                return;

            this.relativePath = path;
            pathRelation = relativeTo;
            return;
        }

        /// <summary>
        /// Terminates playback of the current sequence and removes it from the player
        /// </summary>
        public void ClearSequence()
        {
            stream.Dispose();
        }

        /// <summary>
        /// Start Playback from the current location. 
        /// </summary>
        public void Play()
        {
            play = true;
            playbackEvents.Invoke(this, GSPlayerEvents.Started);
        }

        /// <summary>
        /// Pause current playback
        /// </summary>
        public void Pause()
        {
            play = false;
            playbackEvents.Invoke(this, GSPlayerEvents.Paused);
        }

        /// <summary>
        /// Stops the playback
        /// </summary>
        public void Stop()
        {
            Pause();
            GoToFrame(0);
            playbackEvents.Invoke(this, GSPlayerEvents.Stopped);
        }

        /// <summary>
        /// Activate or deactivate looped playback
        /// </summary>
        /// <param name="enabled"></param>
        public void SetLoopPlay(bool enabled)
        {
            loopPlay = enabled;
        }

        /// <summary>
        /// Activate or deactivate automatic playback (when the scene starts)
        /// </summary>
        /// <param name="enabled"></param>
        public void SetAutoStart(bool enabled)
        {
            playAtStart = false;
        }

        /// <summary>
        /// Seeks to the start of the sequence and then starts playback
        /// </summary>
        public bool PlayFromStart()
        {
            if (GoToFrame(0))
            {
                Play();
                return true;
            }

            return false;
        }

        /// <summary>
        /// Goes to a specific frame. Use GetTotalFrames() to check how many frames the clip contains
        /// </summary>
        /// <param name="frame"></param>
        public bool GoToFrame(int frame)
        {
            if (stream != null)
            {
                float time = (frame * stream.targetFrameTimeMs) / 1000;
                return GoToTime(time);
            }

            return false;
        }

        /// <summary>
        /// Goes to a specific time in  a clip. The time is dependent on the framerate e.g. the same clip at 30 FPS is twice as long as at 60 FPS.
        /// Use GetTotalTime() to see how long the clip is
        /// </summary>
        /// <param name="timeInSeconds"></param>
        /// <returns></returns>
        public bool GoToTime(float timeInSeconds)
        {
            if (timeInSeconds < 0 || timeInSeconds > GetTotalTime())
                return false;

            playbackTimeMs = timeInSeconds * 1000;
            stream.SetFrameTime(playbackTimeMs);
            return true;

        }
        /// <summary>
        /// Sets a new framerate.
        /// This will instantly change the framerate to the desired one.
        /// Use to fasten or slow the playback during runtime.
        /// For initialisation use <see cref="GeometrySequencePlayer.OpenSequence(string, GeometrySequenceStream.PathType, float, bool, bool)"/>
        /// </summary>
        /// <param name="rate">new framerate</param>
        public void SetFrameRate(float rate)
        {
            this.playbackFPS = rate;
            stream.SetPlaybackFPS(rate);
        }

        /// <summary>
        /// Makes the sequence visible
        /// </summary>
        public void Show()
        {
            stream.ShowSequence();
        }

        /// <summary>
        /// Hides the sequence, but still lets it run in the background
        /// </summary>
        public void Hide()
        {
            stream.HideSequence();
        }

        /// <summary>
        /// Sets a uniform opacity over the whole sequence, taking effect immediately.
        /// This is a global multiplier, not per-point alpha from the sequence data.
        /// </summary>
        /// <param name="opacity">0 (invisible) to 1 (fully opaque). Values outside are clamped.</param>
        public void SetOpacity(float opacity)
        {
            //Guarded because a script may set this on a component it has just added, before
            //SetupGeometryStream has run.
            if (stream == null)
                return;

            stream.SetOpacity(opacity);
        }

        /// <summary>
        /// Set the render path for pointclouds. Render path will only change once the
        /// sequence is also being changed or loaded
        /// </summary>
        /// <param name="renderPath"></param>
        public void SetRenderPath(GeometrySequenceStream.PointcloudRenderPath renderPath)
        {
            stream.pointRenderPath = renderPath;
        }

        /// <summary>
        /// Gets the absolute path to the folder containing the sequence
        /// </summary>
        /// <returns></returns>
        public string GetAbsoluteSequencePath()
        {
            string absolutePath = "";

            //Set the correct absolute path depending on the files location
            if (pathRelation == GeometrySequenceStream.PathType.RelativeToDataPath)
                absolutePath = Path.Combine(UnityEngine.Application.dataPath, this.relativePath);

            if (pathRelation == GeometrySequenceStream.PathType.RelativeToStreamingAssets)
                absolutePath = Path.Combine(UnityEngine.Application.streamingAssetsPath, this.relativePath);

            if (pathRelation == GeometrySequenceStream.PathType.RelativeToPersistentDataPath)
                absolutePath = Path.Combine(UnityEngine.Application.persistentDataPath, this.relativePath);

            if (pathRelation == GeometrySequenceStream.PathType.AbsolutePath)
                absolutePath = this.relativePath;


            return absolutePath;
        }

        /// <summary>
        /// Gets the relative path to the sequence directory. Get the path which it is relative to with GetRelativeTo()
        /// </summary>
        /// <returns></returns>
        public string GetRelativeSequencePath()
        {
            return relativePath;
        }

        /// <summary>
        /// Get the location to which the relativePath is relative to.
        /// </summary>
        /// <returns>The relative location.</returns>
        public GeometrySequenceStream.PathType GetRelativeTo()
        {
            return pathRelation;
        }

        /// <summary>
        /// Is a sequence currently loaded and ready to play?
        /// </summary>
        /// <returns>True if a sequence is initialized and ready to play, false if not</returns>
        public bool IsInitialized()
        {
            return stream.readerInitialized;
        }



        /// <summary>
        /// Is the current clip playing?
        /// </summary>
        /// <returns></returns>
        public bool IsPlaying()
        {
            return play;
        }

        /// <summary>
        /// Is looped playback enabled?
        /// </summary>
        /// <returns></returns>
        public bool GetLoopingEnabled()
        {
            return loopPlay;
        }

        /// <summary>
        /// At which frame is the playback currently?
        /// </summary>
        /// <returns></returns>
        public int GetCurrentFrameIndex()
        {
            if (stream != null)
                return stream.lastFrameIndex;
            return -1;
        }

        /// <summary>
        /// At which time is the playback currently in seconds?
        /// Note that the time is dependent on the framerate e.g. the same clip at 30 FPS is twice as long as at 60 FPS.
        /// </summary>
        /// <returns></returns>
        public float GetCurrentTime()
        {
            return playbackTimeMs / 1000;
        }

        /// <summary>
        /// How many frames are there in total in the whole sequence?
        /// </summary>
        /// <returns></returns>
        public int GetTotalFrames()
        {
            if (stream != null)
                if (stream.bufferedReader != null)
                    return stream.bufferedReader.totalFrames;
            return -1;
        }

        /// <summary>
        /// How long is the sequence in total?
        /// Note that the time is dependent on the framerate e.g. the same clip at 30 FPS is twice as long as at 60 FPS.
        /// </summary>
        /// <returns></returns>
        public float GetTotalTime()
        {
            return GetTotalFrames() / GetTargetFPS();
        }

        /// <summary>
        /// The target fps is the framerate we _want_ to achieve in playback. However, this is not guranteed, if system resources
        /// are too low. Use GetActualFPS() to see if you actually achieve this framerate
        /// </summary>
        /// <returns></returns>
        public float GetTargetFPS()
        {
            if (stream != null)
                return 1000 / stream.targetFrameTimeMs;
            return -1;
        }

        /// <summary>
        /// What is the actual current playback framerate? If the framerate is much lower than the target framerate,
        /// consider reducing the complexity of your sequence, and don't forget to disable any V-Sync (VSync, FreeSync, GSync) methods!
        /// </summary>
        /// <returns></returns>
        public float GetActualFPS()
        {
            if (stream != null)
                return stream.smoothedFPS;
            return -1;
        }

        /// <summary>
        /// Check if there have been framedrops since you last checked this function
        /// Too many framedrops mean the system can't keep up with the playback,
        /// and you should reduce your Geometric complexity or framerate
        /// </summary>
        /// <returns></returns>
        public bool GetFrameDropped()
        {
            if (stream != null)
            {
                bool dropped = stream.frameDropped;
                stream.frameDropped = false;
                return dropped;
            }

            return false;
        }

        /// <summary>
        /// Checks if the playback cache has been filled and is ready to play.
        /// If it is, playback starts immediatly once you call Play()
        /// </summary>
        /// <returns></returns>
        public bool IsCacheFilled()
        {
            if (stream != null)
            {
                if (stream.bufferedReader != null)
                {
                    bool cacheReady = true;

                    for (int i = 0; i < stream.bufferedReader.frameBuffer.Length; i++)
                    {
                        if (!stream.bufferedReader.IsFrameBuffered(stream.bufferedReader.frameBuffer[i]))
                            cacheReady = false;
                    }

                    return cacheReady;
                }
            }

            return false;
        }

        /// <summary>
        /// Used to wait until buffering has been completed before starting the sequence
        /// </summary>
        /// <param name="playFromStart"></param>
        /// <returns></returns>

        private IEnumerator WaitForBufferingCompleted(bool playFromStart)
        {
            if (bufferingSequence)
            {
                while (!IsCacheFilled())
                    yield return null;

                bufferingSequence = false;

                playbackEvents.Invoke(this, GSPlayerEvents.BufferingCompleted);

                if (playFromStart)
                {
                    PlayFromStart();
                    playbackEvents.Invoke(this, GSPlayerEvents.PlaybackStarted);
                }
            }

        }

        /// <summary>
        /// Get the currently set render path. This might not be the actual render path being
        /// used, but could also be the next scheduled render path
        /// </summary>
        /// <returns></returns>
        public GeometrySequenceStream.PointcloudRenderPath GetRenderingPath()
        {
            return stream.pointRenderPath;
        }


        #region Thumbnail
#if UNITY_EDITOR

    //Functions used to show the thumbnail in the Editor
    public void ShowThumbnail(string pathToSequence)
    {
      if (stream == null)
        stream = GetComponent<GeometrySequenceStream>();

      if (pathToSequence.Length == 0)
        return;

      stream.LoadEditorThumbnail(pathToSequence);
    }

    public void ClearThumbnail()
    {
      if (stream == null)
        stream = GetComponent<GeometrySequenceStream>();

      stream.ClearEditorThumbnail();
    }
#endif
        #endregion

        #region Obsolete

        [Obsolete("LoadSequence is deprecated, please use OpenSequence instead.", true)]
        public void LoadSequence()
        { }

        [Obsolete("ReloadSequence is deprecated, please use LoadCurrentSequence instead.", true)]
        public void ReloadSequence()
        { }

        #endregion
    }

}
