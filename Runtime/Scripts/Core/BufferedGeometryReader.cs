using System;
using System.IO;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using Unity.Jobs;
using System.Text;
using System.Linq;
using System.Text.RegularExpressions;
using static BuildingVolumes.Player.SequenceConfiguration;
using Unity.IO.LowLevel.Unsafe;
using Unity.Collections.LowLevel.Unsafe;
using System.Threading;
using Unity.Mathematics;
using Unity.Burst;

namespace BuildingVolumes.Player
{
  public class Frame
  {
    public NativeArray<byte> vertexBufferRaw;
    public NativeArray<byte> vertexIntermediateBuffer;
    public NativeArray<byte> indiceBufferRaw;
    public NativeArray<byte> indiceIntermediateBuffer;
    public NativeArray<byte> textureBufferRaw;

    public SequenceConfiguration sequenceConfiguration;

    public ReadGeometryJob geoJob;
    public JobHandle geoJobHandle;
    public ReadTextureJob textureJob;
    public JobHandle textureJobHandle;
    public DecompressionJob decompressionJob;
    public JobHandle decompressionJobHandle;

    public BufferState bufferState = BufferState.Empty;
    public int readBufferSize;
    public int playbackIndex;
    public float finishedBufferingTime;
  }

  public enum TextureMode { None, Single, PerFrame };

  public enum BufferState { Empty, Consumed, Reading, Loading, Ready, Playing }

  public class BufferedGeometryReader
  {
    public string folder;
    public SequenceConfiguration sequenceConfig;
    public string[] plyFilePaths;
    public string[] texturesFilePathDDS;
    public string[] texturesFilePathASTC;
    public int bufferSize = 4;
    public int totalFrames;
    public int frameCount = 0;
    public Frame[] frameBuffer;

    public GameObject streamParent;
    public Material materialSource;

    private bool _buffering = true;

    /// <summary>
    /// Create a new buffered reader. 
    /// </summary>
    public BufferedGeometryReader()
    {
    }

    ~BufferedGeometryReader()
    {
      //When the reader is destroyed, we need to ensure that all the NativeArrays will also be manually deleted/disposed
      DisposeFrameBuffer(true);
    }

    /// <summary>
    /// Use this function to set up a new buffered Reader.
    /// </summary>
    /// <param name="folderPath">A path to a folder containing .ply geometry files and optionally .dds texture files</param>
    /// <param name="frameBufferSize">Number of frames to buffer</param>
    /// <returns>Returns true on success, false when any errors have occured during setup</returns>
    public bool SetupReader(string folderPath, int frameBufferSize)
    {
      this.folder = folderPath;

      sequenceConfig = LoadConfigFromFile(folderPath);
      if (sequenceConfig == null)
        return false;

      try
      {
        //Add a temporary padding to the file list, as otherwise the file order will be messed up
        plyFilePaths = new List<string>(Directory.GetFiles(folderPath, "*.ply")).OrderBy(file =>
        Regex.Replace(file, @"\d+", match => match.Value.PadLeft(9, '0'))).ToArray();
      }

      catch (Exception e)
      {
        Debug.LogError("Sequence path is not valid or has restricted access! Path: " + folderPath + " Error: " + e.Message);
        return false;
      }

      if (plyFilePaths.Length == 0)
      {
        Debug.LogError("No .ply files in the sequence directory: " + folderPath);
        return false;
      }

      if (plyFilePaths.Length != sequenceConfig.verticeCounts.Count)
      {
        Debug.LogError("Could not find all required .ply files, make sure your sequence doesn't miss any!");
        return false;
      }

      if (sequenceConfig.textureMode != SequenceConfiguration.TextureMode.None)
      {
        if (sequenceConfig.DDS && GetDeviceDependentTextureFormat() == SequenceConfiguration.TextureFormat.DDS)
        {
          try
          {
            //Add a temporary padding to the file list, as otherwise the file order will be messed up
            texturesFilePathDDS = new List<string>(Directory.GetFiles(folderPath, "*.dds")).OrderBy(file =>
            Regex.Replace(file, @"\d+", match => match.Value.PadLeft(9, '0'))).ToArray();
          }

          catch (Exception e)
          {
            Debug.LogError("Sequence path is not valid or has restricted access! Path: " + folderPath + " Error: " + e.Message);
            return false;
          }

          if (texturesFilePathDDS.Length == 0)
          {
            Debug.LogError("No .dds texture files (for desktop devices) could be found! Make sure that you converted and uploaded .dds textures for this device!");
            return false;
          }

          if (sequenceConfig.textureMode == SequenceConfiguration.TextureMode.PerFrame)
          {
            if (texturesFilePathDDS.Length != sequenceConfig.verticeCounts.Count)
            {
              Debug.LogError("Could not find all required .dds texture files, make sure your sequence doesn't miss any!");
              return false;
            }
          }
        }

        if (sequenceConfig.ASTC && GetDeviceDependentTextureFormat() == SequenceConfiguration.TextureFormat.ASTC)
        {
          try
          {
            //Add a temporary padding to the file list, as otherwise the file order will be messed up
            texturesFilePathASTC = new List<string>(Directory.GetFiles(folderPath, "*.astc")).OrderBy(file =>
            Regex.Replace(file, @"\d+", match => match.Value.PadLeft(9, '0'))).ToArray();
          }

          catch (Exception e)
          {
            Debug.LogError("Sequence path is not valid or has restricted access! Path: " + folderPath + " Error: " + e.Message);
            return false;
          }

          if (texturesFilePathASTC.Length == 0)
          {
            Debug.LogError("No .astc texture files (for mobile devices) could be found! Make sure that you converted and uploaded .astc texture files to this device!");
            return false;
          }

          if (sequenceConfig.textureMode == SequenceConfiguration.TextureMode.PerFrame)
          {
            if (texturesFilePathASTC.Length != sequenceConfig.verticeCounts.Count)
            {
              Debug.LogError("Could not find all required .atsc texture files, make sure your sequence doesn't miss any!");
              return false;
            }
          }

#if UNITY_EDITOR
          if (texturesFilePathASTC.Length > 0 && texturesFilePathDDS.Length == 0)
          {
            Debug.LogError("Only .astc texture files for mobile devices have been found in your sequence." +
                "Astc Textures cannot not be displayed in the editor. To display textures in the editor" +
                "please additionally generate .dds textures with the converter utility!");
          }
#endif
        }



      }

      bufferSize = frameBufferSize;
      totalFrames = plyFilePaths.Length;


      if (bufferSize > totalFrames)
        bufferSize = totalFrames - 1;

      if (bufferSize < 1)
        bufferSize = 1;

      frameBuffer = new Frame[bufferSize];

      for (int i = 0; i < frameBuffer.Length; i++)
      {
        frameBuffer[i] = new Frame();
        frameBuffer[i].sequenceConfiguration = sequenceConfig;
        AllocateFrame(frameBuffer[i], sequenceConfig, i);
        frameBuffer[i].playbackIndex = -1;
      }

      return true;

    }

    /// <summary>
    /// Loads new frames in the buffer if there are free slots. Call this every frame
    /// </summary>
    public void BufferFrames(int targetPlaybackIndex, int lastPlaybackIndex)
    {

      if (!_buffering)
        return;

      if (targetPlaybackIndex < 0 || targetPlaybackIndex > totalFrames)
        return;

      //Mark frames from buffer that are outside our current buffer range
      //as okay to be overwritten, which keeps our buffer moving forward in case of skips or lags
      DeletePastFrames(targetPlaybackIndex, lastPlaybackIndex);

      //Find out which frames we need to buffer. The buffer is a ring
      //buffer, so that when the playback loops, the whole clip doesn't need
      //to reload, but the frames should be ready.
      List<int> framesToBuffer = new List<int>();

      //Look for the frames we could potentially buffer
      for (int i = 0; i < totalFrames; i++)
      {
        //In case our buffer is larger than the whole sequence
        if (framesToBuffer.Count >= totalFrames)
          continue;

        if (targetPlaybackIndex >= totalFrames)
          targetPlaybackIndex = 0;

        int bufferIndex = GetBufferIndex(targetPlaybackIndex);
        if (bufferIndex == -1) //The frame is not already buffered
        {
          framesToBuffer.Add(targetPlaybackIndex);
          //Debug.Log("Buffer requested for frame " + targetPlaybackIndex + " in buffer " + i);
        }

        targetPlaybackIndex++;
      }

      //Check if we have any free buffer space to buffer more frames
      foreach (Frame frame in frameBuffer)
      {
        //Check if the buffer is ready to load the next frame 
        if (frame.bufferState is BufferState.Consumed or BufferState.Empty && framesToBuffer.Count > 0)
        {
          int newPlaybackIndex = framesToBuffer[0];

          if (newPlaybackIndex < totalFrames)
          {
            //Debug.Log("Buffering Frame: " + newPlaybackIndex + " at buffer " + i);
            ScheduleFrame(frame, newPlaybackIndex);
            framesToBuffer.Remove(newPlaybackIndex);
          }
        }
      }

      JobHandle.ScheduleBatchedJobs();

      CheckFramesForCompletion();
    }


    public void ScheduleFrame(Frame frame, int newPlaybackIndex)
    {
      SetupFrameForReading(frame, sequenceConfig, newPlaybackIndex);
      ScheduleGeometryReadJob(frame, plyFilePaths[newPlaybackIndex]);
      if (sequenceConfig.textureMode == SequenceConfiguration.TextureMode.PerFrame)
        ScheduleTextureReadJob(frame, GetDeviceDependentTexturePath(newPlaybackIndex));

    }

    public void CheckFramesForCompletion()
    {
      foreach (Frame frame in frameBuffer)
      {
        if (IsFrameBuffered(frame) && frame.bufferState == BufferState.Reading)
          frame.bufferState = BufferState.Ready;
      }
    }

    public void SetupFrameForReading(Frame frame, SequenceConfiguration config, int index)
    {
      frame.sequenceConfiguration = config;
      frame.playbackIndex = index;
      frame.bufferState = BufferState.Reading;
    }

    void AllocateFrame(Frame frame, SequenceConfiguration config, int bufferIndex)
    {
      frame.sequenceConfiguration.geometryType = config.geometryType;
      frame.geoJob = new ReadGeometryJob();
      frame.textureJob = new ReadTextureJob();
      frame.decompressionJob = new DecompressionJob();

      //Allocate every frame with the highest amount of vertices and indices being used in this sequence. 
      //This way, we can re-use the meshArrays, instead of re-allocating them each frame

      //The vertex buffer that will be read by the GPU / Unitys Rendering system
      int vertexSizeBytes = 3 * 4; //3 vertex position float32
      if (config.hasNormals)
        vertexSizeBytes += 3 * 4; //3 vertex normal float32
      if(config.hasUVs)
        vertexSizeBytes += 2 * 4; //2 UV coordinate float32
      if(config.geometryType == GeometryType.Point)
        vertexSizeBytes += 1 * 4; //1 uint32 of color
      frame.vertexBufferRaw = new NativeArray<byte>(config.maxVertexCount * vertexSizeBytes, Allocator.Persistent);

      //If we use compression, we use an intermediate buffer for the compressed data
      if (config.useCompression)
      {
        int vertexIntermediateSizeBytes = 3 * 2; //3 vertex position float16 
        if (config.hasNormals)
          vertexIntermediateSizeBytes += 3 * 2; //3 vertex normal float16
        if (config.hasUVs)
          vertexIntermediateSizeBytes += 2 * 2; //2 UV coordinate float16
        if (config.geometryType == GeometryType.Point)
          vertexIntermediateSizeBytes += 3; //3 bytes of color
        frame.vertexIntermediateBuffer = new NativeArray<byte>(config.maxVertexCount * vertexIntermediateSizeBytes, Allocator.Persistent);
      }

      else
        frame.vertexIntermediateBuffer = new NativeArray<byte>(0, Allocator.Persistent);

      if (config.geometryType == GeometryType.Point)
      {
        frame.indiceBufferRaw = new NativeArray<byte>(1, Allocator.Persistent);
        frame.indiceIntermediateBuffer = new NativeArray<byte>(1, Allocator.Persistent);
      }

      else
      {
        frame.indiceBufferRaw = new NativeArray<byte>(config.maxIndiceCount * 4, Allocator.Persistent);
        frame.indiceIntermediateBuffer = new NativeArray<byte>((config.maxIndiceCount * 4) + config.maxIndiceCount, Allocator.Persistent);
      }

      frame.sequenceConfiguration.textureMode = config.textureMode;
      if (config.textureMode == SequenceConfiguration.TextureMode.PerFrame)
        frame.textureBufferRaw = new NativeArray<byte>(GetDeviceDependentTextureSize(config), Allocator.Persistent);
      else if (config.textureMode == SequenceConfiguration.TextureMode.Single && frame.playbackIndex == 0)
        frame.textureBufferRaw = new NativeArray<byte>(GetDeviceDependentTextureSize(config), Allocator.Persistent);
      else
        frame.textureBufferRaw = new NativeArray<byte>(1, Allocator.Persistent);

    }

    /// <summary>
    /// Marks frames that are in the past of current Frame index
    /// Should be regularly called to keep the buffer clean in case of skips/lag
    /// </summary>
    /// <param name="targetPlaybackIndex">The currently shown/played back frame</param>
    /// <param name="lastPlaybackIndex">The last shown/played back frame</param>
    public void DeletePastFrames(int targetPlaybackIndex, int lastPlaybackIndex)
    {
      //We want to keep all frames in the buffer, which are one buffersize ahead of
      //the target Frame, as these will be played soon. Outside of that range, all frames can be deleted 
      int targetMaxFrame = targetPlaybackIndex + bufferSize;
      if (targetMaxFrame >= totalFrames)
        targetMaxFrame = targetMaxFrame % totalFrames;

      foreach (Frame frame in frameBuffer)
      {
        bool dispose = false;
        int playbackIndex = frame.playbackIndex;

        //If the target max frame has already looped around the ringbuffer
        if (targetMaxFrame < targetPlaybackIndex)
        {
          if (playbackIndex < targetPlaybackIndex && playbackIndex > targetMaxFrame)
            dispose = true;
        }

        else
        {
          if (playbackIndex < targetPlaybackIndex || playbackIndex > targetMaxFrame)
            dispose = true;
        }

        if (dispose && playbackIndex != lastPlaybackIndex)
        {
          frame.bufferState = BufferState.Empty;
          //Debug.Log("Deleting frame " + playbackIndex + " from Buffer " + i);
        }
      }
    }

    /// <summary>
    /// Check if the desired input frame of the sequence has already been buffered.
    /// </summary>
    /// <param name="playbackIndex">The desired frame number from the whole sequence</param>
    /// <returns>If the frame could be found and has been loaded, you get the index of the frame in the buffer. Returns -1 if frame could not be found or has not been loaded yet</returns>
    public int GetBufferIndexForLoadedPlaybackIndex(int playbackIndex)
    {
      for (int i = 0; i < frameBuffer.Length; i++)
      {
        if (frameBuffer[i].playbackIndex == playbackIndex)
        {
          if (frameBuffer[i].bufferState == BufferState.Ready)
          {
            return i;
          }
          else
            return -1;
        }
      }

      return -1;
    }

    public int GetBufferIndex(int playbackIndex)
    {
      for (int i = 0; i < frameBuffer.Length; i++)
      {
        if (frameBuffer[i].playbackIndex == playbackIndex)
          return i;
      }

      return -1;
    }


    /// <summary>
    /// Get the total amount of frames that are fully stored in buffer
    /// After skipping or loading in a new sequence, it's useful to wait 
    /// until the buffer has stored at least a few frames
    /// </summary>
    /// <returns></returns>
    public int GetBufferedFrames()
    {
      int loadedFrames = 0;

      foreach (Frame frame in frameBuffer)
      {
        if (IsFrameBuffered(frame))
          loadedFrames++;
      }

      return loadedFrames;
    }

    /// <summary>
    /// Has the data loading finished for this frame?
    /// </summary>
    /// <param name="frame"></param>
    /// <returns></returns>
    public bool IsFrameBuffered(Frame frame)
    {
      if (frame.geoJobHandle.IsCompleted)
      {
        if (sequenceConfig.textureMode == SequenceConfiguration.TextureMode.PerFrame)
        {
          if (frame.textureJobHandle.IsCompleted)
            return true;
        }

        else
          return true;
      }

      return false;
    }


    public int GetDeviceDependentTextureSize(SequenceConfiguration configuration)
    {
      SequenceConfiguration.TextureFormat format = GetDeviceDependentTextureFormat();

      switch (format)
      {
        case SequenceConfiguration.TextureFormat.DDS:
          return configuration.textureSizeDDS;
        case SequenceConfiguration.TextureFormat.ASTC:
          return configuration.textureSizeASTC;
        case SequenceConfiguration.TextureFormat.NotSupported:
        default:
          return 0;
      }
    }

    public string GetDeviceDependentTexturePath(int playbackIndex)
    {
      SequenceConfiguration.TextureFormat format = GetDeviceDependentTextureFormat();

      switch (format)
      {
        case SequenceConfiguration.TextureFormat.DDS:
          if (texturesFilePathDDS == null)
            return "";
          if (texturesFilePathDDS.Length < playbackIndex)
            return "";
          return texturesFilePathDDS[playbackIndex];
        case SequenceConfiguration.TextureFormat.ASTC:
          if (texturesFilePathASTC == null)
            return "";
          if (texturesFilePathASTC.Length < playbackIndex)
            return "";
          return texturesFilePathASTC[playbackIndex];
        case SequenceConfiguration.TextureFormat.NotSupported:
        default:
          return "";
      }
    }




    /// <summary>
    /// Schedules a Job that reads a .ply Pointcloud or mesh file from disk
    /// and loads it into memory.
    /// </summary>
    /// <param name="frame">The frame into which to load the data. The mesh data array needs to be initialized already</param>
    /// <param name="plyPath">The absolute path to the .ply file </param>
    /// <returns></returns>
    public void ScheduleGeometryReadJob(Frame frame, string plyPath)
    {
      frame.geoJob.pathCharArray = new NativeArray<byte>(Encoding.UTF8.GetBytes(plyPath), Allocator.Persistent);
      frame.geoJob.readCmd = new NativeArray<ReadCommand>(1, Allocator.Persistent);
      frame.geoJob.geoType = frame.sequenceConfiguration.geometryType;
      frame.geoJob.hasUVs = frame.sequenceConfiguration.hasUVs;
      frame.geoJob.hasNormals = frame.sequenceConfiguration.hasNormals;
      frame.geoJob.useCompression = frame.sequenceConfiguration.useCompression;
      frame.geoJob.headerSize = frame.sequenceConfiguration.headerSizes[frame.playbackIndex];
      frame.geoJob.vertexCount = frame.sequenceConfiguration.verticeCounts[frame.playbackIndex];
      frame.geoJob.indiceCount = frame.sequenceConfiguration.indiceCounts[frame.playbackIndex];
      frame.geoJob.maxIndiceCount = frame.sequenceConfiguration.maxIndiceCount;
      frame.geoJob.vertexBuffer = frame.vertexBufferRaw;
      frame.geoJob.vertexIntermediateBuffer = frame.vertexIntermediateBuffer;
      frame.geoJob.indiceBuffer = frame.indiceBufferRaw;
      frame.geoJob.indiceIntermediateBuffer = frame.indiceIntermediateBuffer;

      JobHandle geoDeps = frame.geoJobHandle;
      if (frame.sequenceConfiguration.useCompression)
        geoDeps = JobHandle.CombineDependencies(geoDeps, frame.decompressionJobHandle);
      frame.geoJobHandle = frame.geoJob.Schedule(geoDeps);

      if (frame.sequenceConfiguration.useCompression)
      {
        frame.decompressionJob.boundsCenter = frame.sequenceConfiguration.boundsCenter;
        frame.decompressionJob.boundsSize = frame.sequenceConfiguration.boundsSize;
        frame.decompressionJob.vertexBuffer = frame.vertexBufferRaw;
        frame.decompressionJob.vertexIntermediateBuffer = frame.vertexIntermediateBuffer;
        frame.decompressionJob.hasNormals = frame.sequenceConfiguration.hasNormals;
        frame.decompressionJob.hasUVs = frame.sequenceConfiguration.hasUVs;
        frame.decompressionJob.hasVertexColors = frame.sequenceConfiguration.geometryType == GeometryType.Point;

        JobHandle postProcessDeps = JobHandle.CombineDependencies(frame.decompressionJobHandle, frame.geoJobHandle);
        frame.decompressionJobHandle = frame.decompressionJob.Schedule(frame.sequenceConfiguration.verticeCounts[frame.playbackIndex],1024, postProcessDeps);
      }
    }

    /// <summary>
    /// Schedules a job which loads a texture file from disk into memory
    /// </summary>
    /// <param name="frame">The frame data into which the texture will be loaded. The textureBufferRaw needs to be initialized already </param>
    /// <param name="texturePath"></param>
    /// <returns></returns>
    public void ScheduleTextureReadJob(Frame frame, string texturePath)
    {
      if (texturePath == "")
      {
        Debug.LogError("Texture Path to read is empty!");
        return;
      }

      frame.textureJob.readCmd = new NativeArray<ReadCommand>(1, Allocator.Persistent);
      frame.textureJob.format = GetDeviceDependentTextureFormat();
      frame.textureJob.textureSize = GetDeviceDependentTextureSize(frame.sequenceConfiguration);
      frame.textureJob.textureRawData = frame.textureBufferRaw;
      frame.textureJob.texturePathCharArray = new NativeArray<byte>(Encoding.UTF8.GetBytes(texturePath), Allocator.Persistent);
      frame.textureJobHandle = frame.textureJob.Schedule(frame.textureJobHandle);
    }


    /// <summary>
    /// This function ensures that all memory resources are unlocated
    /// and all jobs are finished, so that no memory leaks occur.
    /// </summary>
    public void DisposeFrameBuffer(bool stopBuffering)
    {
      _buffering = !stopBuffering;

      if (frameBuffer != null)
      {
        foreach (Frame frame in frameBuffer)
        {
          frame.geoJobHandle.Complete();
          frame.decompressionJobHandle.Complete();
          frame.vertexBufferRaw.Dispose();
          frame.vertexIntermediateBuffer.Dispose();
          frame.indiceBufferRaw.Dispose();
          frame.indiceIntermediateBuffer.Dispose();

          frame.textureJobHandle.Complete();
          frame.textureBufferRaw.Dispose();
        }
      }

      frameBuffer = null;
    }
  }

  public struct ReadGeometryJob : IJob
  {
    [WriteOnly] public NativeArray<byte> vertexIntermediateBuffer;
    [WriteOnly] public NativeArray<byte> vertexBuffer;
    public NativeArray<byte> indiceIntermediateBuffer;
    [WriteOnly] public NativeArray<byte> indiceBuffer;
    [ReadOnly] public bool hasUVs;
    [ReadOnly] public bool hasNormals;
    [ReadOnly] public bool useCompression;
    [ReadOnly] public bool readFinished;
    [ReadOnly] public int headerSize;
    [ReadOnly] public int vertexCount;
    [ReadOnly] public int indiceCount;
    [ReadOnly] public int maxIndiceCount;
    [ReadOnly] public GeometryType geoType;

    public NativeArray<byte> pathCharArray;

    public NativeArray<ReadCommand> readCmd;

    public void Execute()
    {
      readFinished = false;

      //We can't give Lists/strings to a job directly, so we need this workaround 
      byte[] pathCharBuffer = new byte[pathCharArray.Length];
      pathCharArray.CopyTo(pathCharBuffer);
      string path = Encoding.UTF8.GetString(pathCharBuffer);
      pathCharArray.Dispose();

      ReadCommand readVerticesCmd;
      ReadHandle readVerticesHandle;
      readVerticesCmd.Offset = headerSize;
      if (useCompression)
        unsafe { readVerticesCmd.Buffer = vertexIntermediateBuffer.GetUnsafePtr(); }
      else
        unsafe { readVerticesCmd.Buffer = vertexBuffer.GetUnsafePtr(); }

      //Size of the vertice positions
      readVerticesCmd.Size = useCompression ? 3 * 2 : 3 * 4;
      //Add vertex color size
      if (geoType == GeometryType.Point)
        readVerticesCmd.Size += useCompression ? 3 : 4;
      if (hasUVs)
        readVerticesCmd.Size += useCompression ? 2 * 2 : 2 * 4;
      if (hasNormals)
        readVerticesCmd.Size += useCompression? 3 * 2 : 3 * 4;

      readVerticesCmd.Size *= vertexCount;

      readCmd[0] = readVerticesCmd;

      unsafe { readVerticesHandle = AsyncReadManager.Read(path, (ReadCommand*)readCmd.GetUnsafePtr(), 1); }

      if (readVerticesHandle.IsValid())
      {
        while (readVerticesHandle.Status == ReadStatus.InProgress)
        {
          Thread.Sleep(1);
        }
      }

      readVerticesHandle.Dispose();

      if (geoType != GeometryType.Point)
      {
        //Reading the index is a bit more tricky because each index line contains the number of indices in that line, which we don't want to include
        //So we first read it into a temporary array, and then copy only the indices

        ReadCommand readIndicesCmd;
        ReadHandle readIndicesHandle;
        readIndicesCmd.Offset = headerSize + readVerticesCmd.Size;
        readIndicesCmd.Size = (indiceCount * 4) + indiceCount;
        unsafe { readIndicesCmd.Buffer = indiceIntermediateBuffer.GetUnsafePtr(); }
        readCmd[0] = readIndicesCmd;
        unsafe { readIndicesHandle = AsyncReadManager.Read(path, (ReadCommand*)readCmd.GetUnsafePtr(), 1); }

        if (readIndicesHandle.IsValid())
        {
          while (readIndicesHandle.Status == ReadStatus.InProgress)
          {
            Thread.Sleep(1);
          }
        }

        readIndicesHandle.Dispose();

        int indiceTriplet = 3 * 4;

        for (int i = 0; i < indiceCount / 3; i++)
        {
          NativeArray<byte>.Copy(indiceIntermediateBuffer, (i * (indiceTriplet + 1)) + 1, indiceBuffer, i * indiceTriplet, indiceTriplet);
        }
      }

      readFinished = true;
      readCmd.Dispose();
    }
  }

  public struct ReadTextureJob : IJob
  {
    public NativeArray<byte> textureRawData;
    public int textureSize;
    public bool readFinished;
    public SequenceConfiguration.TextureFormat format;

    public NativeArray<byte> texturePathCharArray;

    public NativeArray<ReadCommand> readCmd;

    public void Execute()
    {
      readFinished = false;

      byte[] texturePathCharBuffer = new byte[texturePathCharArray.Length];
      texturePathCharArray.CopyTo(texturePathCharBuffer);
      string texturePath = Encoding.UTF8.GetString(texturePathCharBuffer);
      texturePathCharArray.Dispose();

      int headerSize = 0;
      if (format == SequenceConfiguration.TextureFormat.DDS)
        headerSize = 128;
      if (format == SequenceConfiguration.TextureFormat.ASTC)
        headerSize = 16;

      ReadCommand readTextureCmd;
      ReadHandle readTextureHandle;
      readTextureCmd.Offset = headerSize;
      readTextureCmd.Size = textureSize;
      unsafe { readTextureCmd.Buffer = textureRawData.GetUnsafePtr(); }


      readCmd[0] = readTextureCmd;

      unsafe { readTextureHandle = AsyncReadManager.Read(texturePath, (ReadCommand*)readCmd.GetUnsafePtr(), 1); }

      if (readTextureHandle.IsValid())
      {
        while (readTextureHandle.Status == ReadStatus.InProgress)
        {
          Thread.Sleep(1);
        }
      }

      readTextureHandle.Dispose();

      readFinished = true;
      readCmd.Dispose();
    }
  }

  [BurstCompile]
  public struct DecompressionJob : IJobParallelFor
  {
    [ReadOnly] public Vector3 boundsCenter;
    [ReadOnly] public Vector3 boundsSize;
    [ReadOnly] public bool hasNormals;
    [ReadOnly] public bool hasUVs;
    [ReadOnly] public bool hasVertexColors;
    
    [ReadOnly] public NativeArray<byte> vertexIntermediateBuffer;
    [WriteOnly] public NativeArray<byte> vertexBuffer;

    public unsafe void Execute(int index)
    {
      int compressedVertexByteSize = 6;
      if (hasVertexColors)
        compressedVertexByteSize += 3;
      if (hasNormals)
        compressedVertexByteSize += 6;
      if (hasUVs)
        compressedVertexByteSize += 4;

      int uncompressedVertexByteSize = 12;
      if (hasVertexColors)
        uncompressedVertexByteSize += 4;
      if (hasNormals)
        uncompressedVertexByteSize += 12;
      if (hasUVs)
        uncompressedVertexByteSize += 8;


      byte* src = (byte*)vertexIntermediateBuffer.GetUnsafeReadOnlyPtr() + index*compressedVertexByteSize;

      //Read position halfs
      float x = *(half*)(src + 0) * boundsSize.x + boundsCenter.x;
      float y = *(half*)(src + 2) * boundsSize.y + boundsCenter.y;
      float z = *(half*)(src + 4) * boundsSize.z + boundsCenter.z;
      src += 6;

      float nx = 0, ny = 0, nz = 0;
      if (hasNormals)
      {
        nx = *(half*)(src + 0);
        ny = *(half*)(src + 2);
        nz = *(half*)(src + 4);
        src += 6;
      }

      byte r = 0, g = 0, b = 0;
      if(hasVertexColors)
      {
        r = *(src + 0);
        g = *(src + 1);
        b = *(src + 2);
        src += 3;
      }

      float u = 0, v = 0;
      if (hasUVs)
      {
        u = *(half*)(src + 0);
        v = *(half*)(src + 2);
      }


      byte* dst = (byte*)vertexBuffer.GetUnsafePtr() + index*uncompressedVertexByteSize;

      //Write position floats      
      *(float*)(dst + 0) = x;
      *(float*)(dst + 4) = y;
      *(float*)(dst + 8) = z;
      dst += 12;

      //Write optional normal floats
      if (hasNormals)
      {
        *(float*)(dst + 0) = nx;
        *(float*)(dst + 4) = ny;
        *(float*)(dst + 8) = nz;
        dst += 12;
      }

      //Write color bytes and add empty alpha channel for RGBA format
      if (hasVertexColors)
      {
        *(dst + 0) = r;
        *(dst + 1) = g;
        *(dst + 2) = b;
        *(dst + 3) = 0;
        dst += 4;
      }

      if (hasUVs)
      {
        *(float*)(dst + 0) = u;
        *(float*)(dst + 4) = v;
      }
      
    }
  }
}
