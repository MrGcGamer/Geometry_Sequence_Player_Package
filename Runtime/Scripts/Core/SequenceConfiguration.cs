using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace BuildingVolumes.Player
{
    public class SequenceConfiguration
    {
        public enum GeometryType { Point = 0, Mesh = 1, TexturedMesh = 2 };
        public enum TextureMode { None = 0, Single = 1, PerFrame = 2 };
        public enum TextureFormat { NotSupported = 0, DDS = 1, ASTC = 2 };
        public enum CompressionMethod { None = 0, Quantized = 1, Draco = 2 };

        public string sequenceVersion;
        public GeometryType geometryType;
        public TextureMode textureMode;
        public bool DDS;
        public bool ASTC;
        public bool hasUVs;
        public bool hasNormals = false;
        public bool useCompression = false;
        public CompressionMethod compressionMethod = CompressionMethod.None;
        public int maxVertexCount;
        public int maxIndiceCount;
        public Vector3 boundsCenter;
        public Vector3 boundsSize;
        public int textureWidth;
        public int textureHeight;
        public int textureSizeDDS;
        public int textureSizeASTC;
        public List<int> headerSizes;
        public List<int> verticeCounts;
        public List<int> indiceCounts;


        public static SequenceConfiguration LoadConfigFromFile(string path)
        {
            string content;

            path += "/sequence.json";

            if (File.Exists(path) && path.Length > 0)
            {
                content = File.ReadAllText(path);
            }

            else
            {
                Debug.LogError("Could not find sequence.json metadata file at: " + path);
                return null;
            }

            SequenceConfiguration configuration;

            try
            {
                configuration = JsonUtility.FromJson<SequenceConfiguration>(content);
            }

            catch (Exception e)
            {
                Debug.LogError("Could not parse metadata file! " + e.Message);
                return null;
            }

            if (configuration.headerSizes.Count == 0 || configuration.verticeCounts.Count == 0)
            {
                Debug.LogError("Metadata file invalid!");
                return null;
            }

            CheckSequenceVersion(configuration.sequenceVersion, path);

            //Back-compat: sequences created before compressionMethod existed only set the
            //useCompression bool, which always meant the quantized codec. Keep both fields in sync.
            if (configuration.compressionMethod == CompressionMethod.None && configuration.useCompression)
                configuration.compressionMethod = CompressionMethod.Quantized;
            //The quantized read/dequantize path must not run for Draco sequences
            configuration.useCompression = configuration.compressionMethod == CompressionMethod.Quantized;

            return configuration;
        }

        public Bounds GetBounds()
        {

            return new Bounds(boundsCenter, boundsSize);

        }

        public static TextureFormat GetDeviceDependentTextureFormat()
        {
#if UNITY_EDITOR || UNITY_STANDALONE_WIN || UNITY_STANDALONE_OSX || UNITY_STANDALONE_LINUX
            return TextureFormat.DDS;
#elif UNITY_IOS || UNITY_ANDROID || UNITY_TVOS || UNITY_VISIONOS
            return TextureFormat.ASTC;
#else
            return TextureFormat.NotSupported;
#endif
        }

        private static bool CheckSequenceVersion(string sequenceVersion, string path)
        {
            bool versionValid = false;

            if (sequenceVersion != null)
                if (sequenceVersion != string.Empty)
                    versionValid = true;

            if (!versionValid)
            {
                Debug.LogError($"Could not validate sequence version of: {path}. Please make sure you've created this sequence using the latest converter version, " +
                  $"otherwise playback might not work correctly! <a href=\"https://github.com/BuildingVolumes/Unity_Geometry_Sequence_Player/releases\" line=\"2\">Update the converter here</a>");
                return false;
            }

            string packageVersion = "1.3.0";

            if (new Version(sequenceVersion).CompareTo(new Version(packageVersion)) < 0)
            {
                Debug.LogError($"The sequence was created with an outdated version of the Sequence Converter, which can lead to playback errors. " +
                 $"Please update the sequence converter and re-convert your sequences!  <a href=\"https://github.com/BuildingVolumes/Unity_Geometry_Sequence_Player/releases\" line=\"2\">Update the converter here</a>");
                return false;
            }

            return true;

        }
    }
}


