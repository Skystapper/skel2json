using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Skel2Json
{
    class Program
    {
        static void Main(string[] args)
        {
            string skelFile = null;
            string jsonFile = null;
            bool includeVertices = true;
            
            // Parse command line arguments
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--no-vertices" || args[i] == "-nv")
                {
                    includeVertices = false;
                }
                else if (skelFile == null)
                {
                    skelFile = args[i];
                }
                else if (jsonFile == null)
                {
                    jsonFile = args[i];
                }
            }
            
            // If no file specified, use default
            if (skelFile == null)
            {
                Console.WriteLine("No input file specified. Usage: Skel2Json <skelFile> [outputJsonFile] [--no-vertices|-nv]");
                return;
            }

            if (jsonFile == null)
            {
                jsonFile = Path.ChangeExtension(skelFile, ".json");
            }

            try
            {
                Console.WriteLine($"Converting {skelFile} to {jsonFile}");
                Console.WriteLine($"Including vertices data: {includeVertices}");
                
                // Get file size for displaying progress
                var fileInfo = new FileInfo(skelFile);
                var fileSizeKB = fileInfo.Length / 1024;
                Console.WriteLine($"Input file size: {fileSizeKB} KB");
                
                // Start time for tracking
                var startTime = DateTime.Now;
                
                // Create a new converter
                var converter = new SkeletonConverter();
                
                // Read the skeleton file
                string jsonOutput = converter.ConvertSkelToJson(skelFile, includeVertices);
                
                // Write the JSON to a file
                File.WriteAllText(jsonFile, jsonOutput);
                
                // Get output file size and time taken
                var outputFileInfo = new FileInfo(jsonFile);
                var outputSizeKB = outputFileInfo.Length / 1024;
                var timeTaken = DateTime.Now - startTime;
                
                Console.WriteLine($"Conversion completed successfully!");
                Console.WriteLine($"Output file size: {outputSizeKB} KB");
                Console.WriteLine($"Time taken: {timeTaken.TotalSeconds:F2} seconds");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }
    }

    public class SkeletonConverter
    {
        public string ConvertSkelToJson(string skelFilePath, bool includeVerticesData = true)
        {
            using (FileStream input = new FileStream(skelFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var binaryReader = new SkeletonBinaryReader(input);
                var skeletonData = binaryReader.ReadSkeletonData();
                
                // Clean up vertices data to remove infinity and NaN values
                CleanupVerticesData(skeletonData);
                
                // Optionally strip vertices and triangles data to reduce output size
                if (!includeVerticesData)
                {
                    RemoveVerticesData(skeletonData);
                }
                
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
                };
                
                // Convert the data to JSON
                string jsonOutput = JsonSerializer.Serialize(skeletonData, options);
                
                // Additional formatting to match Spine JSON exactly
                jsonOutput = jsonOutput.Replace("  ", "    ");  // 4-space indentation
                
                // Convert NaN and Infinity to null
                jsonOutput = jsonOutput.Replace("\"NaN\"", "null");
                jsonOutput = jsonOutput.Replace("\"Infinity\"", "null");
                jsonOutput = jsonOutput.Replace("\"-Infinity\"", "null");
                
                // Fix the double-dot issue in numbers
                jsonOutput = jsonOutput.Replace(".0.0", ".0");
                
                return jsonOutput;
            }
        }

        private void CleanupVerticesData(SkeletonData skeletonData)
        {
            // Clean up any invalid float values in vertices data
            foreach (var skin in skeletonData.Skins)
            {
                foreach (var slotAttachments in skin.Attachments)
                {
                    foreach (var attachmentEntry in slotAttachments.Value)
                    {
                        var attachment = attachmentEntry.Value;
                        if (attachment.Vertices != null)
                        {
                            // Replace Infinity, NaN, etc. with 0
                            for (int i = 0; i < attachment.Vertices.Length; i++)
                            {
                                if (float.IsInfinity(attachment.Vertices[i]) || float.IsNaN(attachment.Vertices[i]))
                                {
                                    attachment.Vertices[i] = 0;
                                }
                            }
                        }
                        
                        // Clean UVs and other arrays
                        if (attachment.Uvs != null)
                        {
                            for (int i = 0; i < attachment.Uvs.Length; i++)
                            {
                                if (float.IsInfinity(attachment.Uvs[i]) || float.IsNaN(attachment.Uvs[i]))
                                {
                                    attachment.Uvs[i] = 0;
                                }
                            }
                        }
                        
                        if (attachment.Lengths != null)
                        {
                            for (int i = 0; i < attachment.Lengths.Length; i++)
                            {
                                if (float.IsInfinity(attachment.Lengths[i]) || float.IsNaN(attachment.Lengths[i]))
                                {
                                    attachment.Lengths[i] = 0;
                                }
                            }
                        }
                    }
                }
            }
        }

        private void RemoveVerticesData(SkeletonData skeletonData)
        {
            // Remove all vertices, triangles, etc. data to reduce the output size
            foreach (var skin in skeletonData.Skins)
            {
                foreach (var slotAttachments in skin.Attachments)
                {
                    foreach (var attachmentEntry in slotAttachments.Value)
                    {
                        var attachment = attachmentEntry.Value;
                        attachment.Vertices = null;
                        attachment.Triangles = null;
                        attachment.Uvs = null;
                        attachment.Edges = null;
                    }
                }
            }
        }
    }

    public class SkeletonBinaryReader
    {
        private SkeletonInput input;
        private Dictionary<int, BoneInfo> bonesLookup = new Dictionary<int, BoneInfo>();
        private List<LinkedMeshInfo> linkedMeshes = new List<LinkedMeshInfo>();

        public SkeletonBinaryReader(Stream input)
        {
            this.input = new SkeletonInput(input);
        }

        // Add helper enum for AttachmentType conversion
        private enum AttachmentType : byte
        {
            Region = 0, 
            BoundingBox = 1, 
            Mesh = 2, 
            LinkedMesh = 3, 
            Path = 4, 
            Point = 5, 
            Clipping = 6, 
            Sequence = 7
        }

        // Helper method to convert AttachmentType enum to string
        private string GetAttachmentTypeString(byte type)
        {
            // Handle all possible values by checking against the enum values
            if (type == (byte)AttachmentType.Region) return "region";
            if (type == (byte)AttachmentType.BoundingBox) return "boundingbox";
            if (type == (byte)AttachmentType.Mesh) return "mesh";
            if (type == (byte)AttachmentType.LinkedMesh) return "linkedmesh";
            if (type == (byte)AttachmentType.Path) return "path";
            if (type == (byte)AttachmentType.Point) return "point";
            if (type == (byte)AttachmentType.Clipping) return "clipping";
            
            // Default for unknown types
            return "unknown";
        }

        public SkeletonData ReadSkeletonData()
        {
            float scale = 1.0f;

            SkeletonData skeletonData = new SkeletonData();
            skeletonData.Bones = new List<BoneData>();
            skeletonData.Slots = new List<SlotData>();
            skeletonData.Ik = new List<IkConstraintData>();
            
            // Read skeleton header
            long hash = input.ReadLong();
            // Create a shorter hash more like the original format
            string hashString = GenerateShortHash(hash);
            
            skeletonData.Skeleton = new SkeletonInfo
            {
                Hash = hashString,
                Spine = input.ReadString() ?? ""
            };
            
            // Skip these values as they're not in the original format
            input.ReadFloat(); // X
            input.ReadFloat(); // Y
            input.ReadFloat(); // Width
            input.ReadFloat(); // Height

            bool nonessential = input.ReadBoolean();

            if (nonessential)
            {
                // fps, imagesPath, audioPath aren't in the original format
                input.ReadFloat(); // Fps
                input.ReadString(); // ImagesPath
                input.ReadString(); // AudioPath
            }

            // Read strings table
            int n = input.ReadInt(true);
            Console.WriteLine($"Reading {n} strings");
            input.strings = new string[n];
            for (int i = 0; i < n; i++)
                input.strings[i] = input.ReadString() ?? "";

            // Read bones
            n = input.ReadInt(true);
            Console.WriteLine($"Reading {n} bones");
            for (int i = 0; i < n; i++)
            {
                string name = input.ReadString() ?? "";
                // Store bone name for parent references
                bonesLookup[i] = new BoneInfo { Name = name, Index = i };
                
                int parentIndex = i == 0 ? -1 : input.ReadInt(true);
                
                // Read the actual bone data values
                float rotation = input.ReadFloat();
                float x = input.ReadFloat() * scale;
                float y = input.ReadFloat() * scale;
                float scaleX = input.ReadFloat();
                float scaleY = input.ReadFloat();
                float shearX = input.ReadFloat();
                float shearY = input.ReadFloat();
                float length = input.ReadFloat() * scale;
                int transformMode = input.ReadInt(true);
                bool skinRequired = input.ReadBoolean();
                
                // Create the bone data object
                var boneData = new BoneData
                {
                    Name = name,
                    Parent = parentIndex == -1 ? null : bonesLookup[parentIndex].Name,
                };
                
                // Only add non-default values to match original format
                if (Math.Abs(x) > 0.01f) boneData.X = FormatFloat(x);
                if (Math.Abs(y) > 0.01f) boneData.Y = FormatFloat(y);
                if (Math.Abs(rotation) > 0.01f) boneData.Rotation = FormatFloat(rotation);
                if (Math.Abs(length) > 0.01f) boneData.Length = FormatFloat(length);
                if (Math.Abs(scaleX - 1) > 0.01f) boneData.ScaleX = FormatFloat(scaleX);
                if (Math.Abs(scaleY - 1) > 0.01f) boneData.ScaleY = FormatFloat(scaleY);
                if (Math.Abs(shearX) > 0.01f) boneData.ShearX = FormatFloat(shearX);
                if (Math.Abs(shearY) > 0.01f) boneData.ShearY = FormatFloat(shearY);
                
                // Convert TransformMode from int to string
                switch (transformMode)
                {
                    case 1:
                        boneData.Transform = "onlyTranslation";
                        break;
                    case 2:
                        boneData.Transform = "noRotationOrReflection";
                        break;
                    case 3:
                        boneData.Transform = "noScale";
                        break;
                    case 4:
                        boneData.Transform = "noScaleOrReflection";
                        break;
                }
                
                // Only set SkinRequired if true
                if (skinRequired)
                    boneData.SkinRequired = true;
                
                if (nonessential) input.ReadInt(); // Skip bone color
                
                skeletonData.Bones.Add(boneData);
            }

            try
            {
                // Read slots
                n = input.ReadInt(true);
                Console.WriteLine($"Reading {n} slots");
                for (int i = 0; i < n; i++)
                {
                    try
                    {
                        string slotName = input.ReadString() ?? "";
                        // Clean up the slot name - remove non-printable characters
                        slotName = CleanString(slotName);
                        
                        int boneIndex = input.ReadInt(true);
                        
                        // Get the bone name - handle invalid index gracefully
                        string boneName = "root"; // Default to root if bone index is invalid
                        if (bonesLookup.TryGetValue(boneIndex, out BoneInfo boneInfo))
                        {
                            boneName = boneInfo.Name;
                        }
                        
                        // Create the slot data object
                        var slotData = new SlotData
                        {
                            Name = slotName,
                            Bone = boneName
                        };
                        
                        // Read color (RGBA format)
                        int color = input.ReadInt();
                        float r = ((color & 0xff000000) >> 24) / 255f;
                        float g = ((color & 0x00ff0000) >> 16) / 255f;
                        float b = ((color & 0x0000ff00) >> 8) / 255f;
                        float a = ((color & 0x000000ff)) / 255f;
                        
                        // Only add color if not default (1,1,1,1)
                        if (r != 1 || g != 1 || b != 1 || a != 1)
                        {
                            slotData.Color = FormatColor(r, g, b, a);
                        }
                        
                        // Read dark color (if present)
                        int darkColor = input.ReadInt(); // 0x00rrggbb
                        if (darkColor != -1)
                        {
                            float r2 = ((darkColor & 0x00ff0000) >> 16) / 255f;
                            float g2 = ((darkColor & 0x0000ff00) >> 8) / 255f;
                            float b2 = ((darkColor & 0x000000ff)) / 255f;
                            
                            // Add dark color
                            slotData.Dark = FormatColor(r2, g2, b2, 1); // Always 1 for alpha in dark color
                        }
                        
                        // Read attachment name using StringRef
                        slotData.Attachment = input.ReadStringRef();
                        
                        // Read blend mode
                        int blendMode = input.ReadInt(true);
                        if (blendMode != 0) // Not normal blend
                        {
                            switch (blendMode)
                            {
                                case 1:
                                    slotData.Blend = "additive";
                                    break;
                                case 2:
                                    slotData.Blend = "multiply";
                                    break;
                                case 3:
                                    slotData.Blend = "screen";
                                    break;
                            }
                        }
                        
                        skeletonData.Slots.Add(slotData);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error reading slot {i}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading slots: {ex.Message}");
                // Continue with what we have so far
            }

            // Read IK constraints
            try
            {
                n = input.ReadInt(true);
                Console.WriteLine($"Reading {n} IK constraints");
                for (int i = 0; i < n; i++)
                {
                    var ikName = input.ReadString();
                    var ikData = new IkConstraintData
                    {
                        Name = ikName
                    };
                    
                    // Read order
                    ikData.Order = input.ReadInt(true);
                    
                    // Read skin required flag - only include in output if true
                    bool skinRequired = input.ReadBoolean();
                    if (skinRequired)
                    {
                        ikData.SkinRequired = true;
                    }
                    
                    // Read bones
                    int bonesCount = input.ReadInt(true);
                    ikData.Bones = new List<string>(bonesCount);
                    for (int j = 0; j < bonesCount; j++)
                    {
                        int boneIndex = input.ReadInt(true);
                        if (bonesLookup.TryGetValue(boneIndex, out BoneInfo boneInfo))
                        {
                            ikData.Bones.Add(boneInfo.Name);
                        }
                    }
                    
                    // Read target
                    int targetIndex = input.ReadInt(true);
                    if (bonesLookup.TryGetValue(targetIndex, out BoneInfo targetBone))
                    {
                        ikData.Target = targetBone.Name;
                    }
                    
                    // Read mix - default value in Spine is 1, so only include if not 1
                    float mix = input.ReadFloat();
                    if (Math.Abs(mix - 1) > 0.01f)
                    {
                        ikData.Mix = FormatFloat(mix);
                    }
                    
                    // Read softness - default value in Spine is 0, so only include if not 0
                    float softness = input.ReadFloat() * scale;
                    if (Math.Abs(softness) > 0.01f)
                    {
                        ikData.Softness = FormatFloat(softness);
                    }
                    
                    // Read bend direction - default value in Spine is 1
                    // According to Spine source code, the value is stored as SByte (int8)
                    sbyte bendDirection = input.ReadSByte();
                    // Only include if not default (1)
                    if (bendDirection != 1)
                    {
                        ikData.BendDirection = bendDirection;
                    }
                    
                    // Read compress flag - default is false
                    bool compress = input.ReadBoolean();
                    if (compress)
                    {
                        ikData.Compress = true;
                    }
                    
                    // Read stretch flag - default is false
                    bool stretch = input.ReadBoolean();
                    if (stretch)
                    {
                        ikData.Stretch = true;
                    }
                    
                    // Read uniform flag - default is false
                    bool uniform = input.ReadBoolean();
                    if (uniform)
                    {
                        ikData.Uniform = true;
                    }
                    
                    skeletonData.Ik.Add(ikData);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading IK constraints: {ex.Message}");
                // Continue with what we have so far
            }

            // Read skins
            try
            {
                // Read default skin
                SkinData defaultSkin = ReadSkin(true, skeletonData, nonessential, scale);
                if (defaultSkin != null)
                {
                    skeletonData.defaultSkin = defaultSkin;
                    skeletonData.Skins.Add(defaultSkin);
                }
                
                // Read named skins
                n = input.ReadInt(true);
                if (n < 0 || n > 1000) // Use a reasonable upper limit
                {
                    Console.WriteLine($"Invalid skin count: {n}. Skipping additional skins.");
                }
                else
                {
                    Console.WriteLine($"Reading {n} additional skins");
                    for (int i = 0; i < n; i++)
                    {
                        SkinData skin = ReadSkin(false, skeletonData, nonessential, scale);
                        if (skin != null)
                        {
                            skeletonData.Skins.Add(skin);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading skins: {ex.Message}");
                // Continue with what we have so far
            }

            // Process linked meshes
            try {
                foreach (LinkedMeshInfo linkedMesh in linkedMeshes) {
                    string skinName = linkedMesh.SkinName;
                    SkinData skin = skinName == null ? skeletonData.defaultSkin : 
                        skeletonData.Skins.FirstOrDefault(s => s.Name == skinName);
                        
                    if (skin == null) {
                        Console.WriteLine($"Skin not found for linked mesh: {skinName}");
                        continue;
                    }
                    
                    // Try to find the parent mesh in the specified skin's attachments
                    AttachmentData parentAttachment = null;
                    foreach (var slotAttachments in skin.Attachments) {
                        if (slotAttachments.Value.TryGetValue(linkedMesh.ParentName, out var attachment)) {
                            parentAttachment = attachment;
                            break;
                        }
                    }
                    
                    if (parentAttachment == null) {
                        Console.WriteLine($"Parent mesh not found for linked mesh: {linkedMesh.ParentName}");
                        continue;
                    }
                    
                    // Copy needed data from parent to linked mesh
                    linkedMesh.Mesh.Uvs = parentAttachment.Uvs;
                    linkedMesh.Mesh.Triangles = parentAttachment.Triangles;
                    linkedMesh.Mesh.HullLength = parentAttachment.HullLength;
                }
            }
            catch (Exception ex) {
                Console.WriteLine($"Error processing linked meshes: {ex.Message}");
            }

            return skeletonData;
        }
        
        // Clean up a string by removing non-printable characters
        private string CleanString(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;
            
            char[] chars = input.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                // Replace non-printable characters with empty string
                if (chars[i] < 32 || chars[i] > 126)
                {
                    chars[i] = '_';
                }
            }
            
            return new string(chars);
        }
        
        // Format float to match the original format (2 decimal places, no trailing zeros)
        private float FormatFloat(float value)
        {
            // Round to 2 decimal places
            float rounded = (float)Math.Round(value, 2);
            return rounded;
        }
        
        // Generate a hash more like the original format
        private string GenerateShortHash(long hash)
        {
            // The original seems to use a base64 encoding that's around 11 characters long
            byte[] hashBytes = BitConverter.GetBytes(hash);
            string base64 = Convert.ToBase64String(hashBytes);
            
            // Strip any padding characters
            base64 = base64.TrimEnd('=');
            
            return base64;
        }
        
        // Format color to #RRGGBBAA format
        private string FormatColor(float r, float g, float b, float a)
        {
            int rInt = (int)(r * 255);
            int gInt = (int)(g * 255);
            int bInt = (int)(b * 255);
            int aInt = (int)(a * 255);
            
            return $"{rInt:X2}{gInt:X2}{bInt:X2}{aInt:X2}";
        }

        private SkinData ReadSkin(bool defaultSkin, SkeletonData skeletonData, bool nonessential, float scale)
        {
            try
            {
                SkinData skin;
                int slotCount;

                if (defaultSkin)
                {
                    slotCount = input.ReadInt(true);
                    if (slotCount == 0) return null;
                    skin = new SkinData { Name = "default" };
                }
                else
                {
                    string skinName = input.ReadStringRef() ?? "unnamed_skin";
                    skin = new SkinData { Name = skinName };
                    Console.WriteLine($"Reading skin: {skinName}");
                    
                    // Read bone references
                    int boneCount = input.ReadInt(true);
                    if (boneCount > 0)
                    {
                        for (int i = 0; i < boneCount; i++)
                        {
                            int boneIndex = input.ReadInt(true);
                            if (bonesLookup.TryGetValue(boneIndex, out BoneInfo boneInfo))
                            {
                                if (skin.Bones == null) skin.Bones = new List<string>();
                                skin.Bones.Add(boneInfo.Name);
                            }
                        }
                    }
                    
                    // Read ik constraint references
                    int ikConstraintsCount = input.ReadInt(true);
                    if (ikConstraintsCount > 0) 
                    {
                        for (int i = 0; i < ikConstraintsCount; i++)
                        {
                            int constraintIndex = input.ReadInt(true);
                            if (constraintIndex >= 0 && constraintIndex < skeletonData.Ik.Count)
                            {
                                if (skin.Constraints == null) skin.Constraints = new List<string>();
                                skin.Constraints.Add(skeletonData.Ik[constraintIndex].Name);
                            }
                        }
                    }
                    
                    // Read transform constraint references - not implemented in this simplified version
                    int transformCount = input.ReadInt(true);
                    for (int i = 0; i < transformCount; i++)
                    {
                        input.ReadInt(true); // Skip transform index
                    }
                    
                    // Read path constraint references - not implemented in this simplified version
                    int pathCount = input.ReadInt(true);
                    for (int i = 0; i < pathCount; i++)
                    {
                        input.ReadInt(true); // Skip path index
                    }
                    
                    slotCount = input.ReadInt(true);
                }

                // Process each slot in the skin
                for (int i = 0; i < slotCount; i++)
                {
                    try
                    {
                        int slotIndex = input.ReadInt(true);
                        string slotName = "unknown";
                        
                        // Find the slot name
                        if (skeletonData.Slots.Count > slotIndex && slotIndex >= 0)
                        {
                            slotName = skeletonData.Slots[slotIndex].Name;
                        }
                        
                        // Initialize the attachments dictionary for this slot if needed
                        if (!skin.Attachments.ContainsKey(slotName))
                        {
                            skin.Attachments[slotName] = new Dictionary<string, AttachmentData>();
                        }
                        
                        int attachmentCount = input.ReadInt(true);
                        
                        // Handle invalid attachment count
                        if (attachmentCount <= 0 || attachmentCount > 1000) // Use a reasonable upper limit
                        {
                            Console.WriteLine($"Invalid attachment count: {attachmentCount} for slot {slotName}. Skipping attachments for this slot.");
                            continue;
                        }
                        
                        // Read each attachment in the slot
                        for (int j = 0; j < attachmentCount; j++)
                        {
                            try
                            {
                                string attachmentName = input.ReadStringRef();
                                if (string.IsNullOrEmpty(attachmentName))
                                {
                                    Console.WriteLine("Null attachment name, skipping.");
                                    continue;
                                }
                                
                                // Read attachment data
                                byte attachmentType = input.ReadByte();
                                
                                // Now properly call ReadAttachment to get the full attachment data
                                AttachmentData attachment = ReadAttachment(attachmentType, attachmentName, slotIndex, scale, skin, nonessential);
                                
                                if (attachment != null)
                                {
                                    skin.Attachments[slotName][attachmentName] = attachment;
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Error reading attachment {j} for slot {slotName}: {ex.Message}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error reading slot {i} for skin: {ex.Message}");
                    }
                }

                return skin;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading skin: {ex.Message}");
                return null;
            }
        }

        private AttachmentData ReadAttachment(byte type, string attachmentName, int slotIndex, float scale, SkinData skin, bool nonessential)
        {
            try
            {
                // Set up the attachment with common properties
                AttachmentData attachment = new AttachmentData
                {
                    Type = GetAttachmentTypeString(type)
                };
                
                switch (type)
                {
                    case (byte)AttachmentType.Region:
                        string path = input.ReadStringRef() ?? attachmentName;
                        attachment.Path = path;
                        
                        // Read region attachment data
                        float rotation = input.ReadFloat();
                        float x = input.ReadFloat() * scale;
                        float y = input.ReadFloat() * scale;
                        float scaleX = input.ReadFloat();
                        float scaleY = input.ReadFloat();
                        float width = input.ReadFloat() * scale;
                        float height = input.ReadFloat() * scale;
                        
                        if (Math.Abs(rotation) > 0.001f) attachment.Rotation = rotation;
                        if (Math.Abs(x) > 0.001f) attachment.X = x;
                        if (Math.Abs(y) > 0.001f) attachment.Y = y;
                        if (Math.Abs(scaleX - 1) > 0.001f) attachment.ScaleX = scaleX;
                        if (Math.Abs(scaleY - 1) > 0.001f) attachment.ScaleY = scaleY;
                        if (width > 0) attachment.Width = width;
                        if (height > 0) attachment.Height = height;
                        
                        // Read color if present
                        int color = input.ReadInt();
                        if (color != -1)
                        {
                            attachment.Color = FormatColor(
                                ((color & 0xff000000) >> 24) / 255f,
                                ((color & 0x00ff0000) >> 16) / 255f,
                                ((color & 0x0000ff00) >> 8) / 255f,
                                ((color & 0x000000ff)) / 255f
                            );
                        }
                        
                        return attachment;
                        
                    case (byte)AttachmentType.BoundingBox:
                        // Read bounding box attachment data
                        int vertexCount = input.ReadInt(true);
                        attachment.VertexCount = vertexCount;
                        ReadVertices(attachment, vertexCount, scale);
                        
                        if (nonessential) 
                        {
                            // Skip color as it's not needed for bounding box
                            input.ReadInt();
                        }
                        
                        return attachment;
                        
                    case (byte)AttachmentType.Mesh:
                    case (byte)AttachmentType.LinkedMesh:
                        path = input.ReadStringRef() ?? attachmentName;
                        attachment.Path = path;
                        
                        // Read color
                        color = input.ReadInt();
                        if (color != -1)
                        {
                            attachment.Color = FormatColor(
                                ((color & 0xff000000) >> 24) / 255f,
                                ((color & 0x00ff0000) >> 16) / 255f,
                                ((color & 0x0000ff00) >> 8) / 255f,
                                ((color & 0x000000ff)) / 255f
                            );
                        }
                        
                        if (type == (byte)AttachmentType.Mesh)
                        {
                            // Read standard mesh
                            int vertCount = input.ReadInt(true);
                            attachment.VertexCount = vertCount;
                            
                            // Read UVs - pair of float values for each vertex
                            float[] uvs = new float[vertCount * 2];
                            for (int i = 0; i < uvs.Length; i++)
                                uvs[i] = input.ReadFloat();
                            attachment.Uvs = uvs;
                            
                            // Read triangles - indices into vertex array
                            int triangleCount = input.ReadInt(true);
                            int[] triangles = new int[triangleCount];
                            for (int i = 0; i < triangleCount; i++)
                                triangles[i] = input.ReadShort() & 0xFFFF; // Convert to unsigned
                            attachment.Triangles = triangles;
                            
                            // Read vertices
                            ReadVertices(attachment, vertCount, scale);
                            
                            // Read hull length
                            int hullLength = input.ReadInt(true);
                            if (hullLength > 0)
                                attachment.HullLength = hullLength * 2;
                            
                            if (nonessential)
                            {
                                // Read edges - used for mesh tesselation
                                int edgesCount = input.ReadInt(true);
                                if (edgesCount > 0)
                                {
                                    int[] edges = new int[edgesCount];
                                    for (int i = 0; i < edgesCount; i++)
                                        edges[i] = input.ReadShort() & 0xFFFF;
                                    attachment.Edges = edges;
                                }
                                
                                // Read width and height
                                width = input.ReadFloat() * scale;
                                height = input.ReadFloat() * scale;
                                if (width > 0) attachment.Width = width;
                                if (height > 0) attachment.Height = height;
                            }
                        }
                        else // LinkedMesh
                        {
                            // Read linked mesh - references another mesh by name
                            string parentName = input.ReadStringRef() ?? "";
                            string skinName = input.ReadStringRef();
                            bool inheritTimelines = input.ReadBoolean();
                            
                            // Add to linked meshes to process after all skins are read
                            linkedMeshes.Add(new LinkedMeshInfo(parentName, skinName, slotIndex, attachment, inheritTimelines));
                            
                            if (nonessential)
                            {
                                width = input.ReadFloat() * scale;
                                height = input.ReadFloat() * scale;
                                if (width > 0) attachment.Width = width;
                                if (height > 0) attachment.Height = height;
                            }
                        }
                        
                        return attachment;
                        
                    case (byte)AttachmentType.Path:
                        // Read path attachment data
                        bool closed = input.ReadBoolean();
                        attachment.Closed = closed;
                        
                        bool constantSpeed = input.ReadBoolean();
                        attachment.ConstantSpeed = constantSpeed;
                        
                        int pathVertCount = input.ReadInt(true);
                        attachment.VertexCount = pathVertCount;
                        ReadVertices(attachment, pathVertCount, scale);
                        
                        // Read lengths array
                        int lengthsCount = input.ReadInt(true);
                        if (lengthsCount > 0)
                        {
                            float[] lengths = new float[lengthsCount];
                            for (int i = 0; i < lengthsCount; i++)
                                lengths[i] = input.ReadFloat() * scale;
                            attachment.Lengths = lengths;
                        }
                        
                        if (nonessential)
                        {
                            // Skip color as it's not needed for path
                            input.ReadInt();
                        }
                        
                        return attachment;
                        
                    case (byte)AttachmentType.Point:
                        // Read point attachment data
                        rotation = input.ReadFloat();
                        x = input.ReadFloat() * scale;
                        y = input.ReadFloat() * scale;
                        
                        if (Math.Abs(rotation) > 0.001f) attachment.Rotation = rotation;
                        if (Math.Abs(x) > 0.001f) attachment.X = x;
                        if (Math.Abs(y) > 0.001f) attachment.Y = y;
                        
                        if (nonessential)
                        {
                            // Skip color as it's not needed for point
                            input.ReadInt();
                        }
                        
                        return attachment;
                        
                    case (byte)AttachmentType.Clipping:
                        // Read clipping attachment data
                        int endSlotIndex = input.ReadInt(true);
                        if (endSlotIndex != -1)
                        {
                            attachment.End = endSlotIndex;
                        }
                        
                        vertexCount = input.ReadInt(true);
                        attachment.VertexCount = vertexCount;
                        ReadVertices(attachment, vertexCount, scale);
                        
                        if (nonessential)
                        {
                            // Skip color as it's not needed for clipping
                            input.ReadInt();
                        }
                        
                        return attachment;
                        
                    default:
                        Console.WriteLine($"Unknown attachment type: {type}");
                        return attachment;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading attachment {attachmentName} of type {type}: {ex.Message}");
                return new AttachmentData { Type = "unknown" };
            }
        }

        private void ReadVertices(AttachmentData attachment, int vertexCount, float scale)
        {
            int verticesLength = vertexCount * 2;
            int bonesCount = input.ReadInt(true);
            
            if (bonesCount == 0)
            {
                // No weights, just vertices
                float[] vertices = new float[verticesLength];
                for (int i = 0; i < verticesLength; i++)
                    vertices[i] = input.ReadFloat() * scale;
                
                attachment.Vertices = vertices;
            }
            else
            {
                // Has weights - format is different in Spine JSON
                // The format expected is: [bone count, bone index, x, y, weight, bone index, x, y, weight, ...]
                List<float> weightsArray = new List<float>();
                
                for (int i = 0; i < vertexCount; i++)
                {
                    int boneCount = input.ReadInt(true);
                    weightsArray.Add(boneCount);
                    
                    for (int j = 0; j < boneCount; j++)
                    {
                        int boneIndex = input.ReadInt(true);
                        float x = input.ReadFloat() * scale;
                        float y = input.ReadFloat() * scale;
                        float weight = input.ReadFloat();
                        
                        weightsArray.Add(boneIndex);
                        weightsArray.Add(x);
                        weightsArray.Add(y);
                        weightsArray.Add(weight);
                    }
                }
                
                // Store weighted vertices
                attachment.Vertices = weightsArray.ToArray();
            }
        }

        private void ReadFloatArray(AttachmentData attachment, string fieldName, int count, float scale)
        {
            float[] array = new float[count];
            for (int i = 0; i < count; i++)
                array[i] = input.ReadFloat() * scale;
            
            // Set the appropriate property based on the field name
            if (fieldName == "uvs")
                attachment.Uvs = array;
        }

        private void ReadShortArray(AttachmentData attachment, string fieldName, int count, float scale)
        {
            int[] array = new int[count];
            for (int i = 0; i < count; i++)
                array[i] = input.ReadShort();
            
            // Set the appropriate property based on the field name
            if (fieldName == "triangles")
                attachment.Triangles = array;
            else if (fieldName == "edges")
                attachment.Edges = array;
        }
    }
    
    // Helper class to store bone info
    public class BoneInfo
    {
        public string Name { get; set; } = string.Empty;
        public int Index { get; set; }
    }

    public class SkeletonInput
    {
        private byte[] chars = new byte[1024]; // Increase buffer size
        private byte[] bytesBigEndian = new byte[8];
        internal string[] strings = Array.Empty<string>();
        Stream input;

        public SkeletonInput(Stream input)
        {
            this.input = input;
        }

        public int Read()
        {
            return input.ReadByte();
        }

        public byte ReadByte()
        {
            return (byte)input.ReadByte();
        }

        public sbyte ReadSByte()
        {
            int value = input.ReadByte();
            if (value == -1) throw new EndOfStreamException();
            return (sbyte)value;
        }

        public bool ReadBoolean()
        {
            return input.ReadByte() != 0;
        }

        public short ReadShort()
        {
            input.Read(bytesBigEndian, 0, 2);
            return (short)((bytesBigEndian[0] << 8) | bytesBigEndian[1]);
        }

        public float ReadFloat()
        {
            input.Read(bytesBigEndian, 0, 4);
            chars[3] = bytesBigEndian[0];
            chars[2] = bytesBigEndian[1];
            chars[1] = bytesBigEndian[2];
            chars[0] = bytesBigEndian[3];
            return BitConverter.ToSingle(chars, 0);
        }

        public int ReadInt()
        {
            input.Read(bytesBigEndian, 0, 4);
            return (bytesBigEndian[0] << 24)
                + (bytesBigEndian[1] << 16)
                + (bytesBigEndian[2] << 8)
                + bytesBigEndian[3];
        }

        public long ReadLong()
        {
            input.Read(bytesBigEndian, 0, 8);
            return ((long)(bytesBigEndian[0]) << 56)
                + ((long)(bytesBigEndian[1]) << 48)
                + ((long)(bytesBigEndian[2]) << 40)
                + ((long)(bytesBigEndian[3]) << 32)
                + ((long)(bytesBigEndian[4]) << 24)
                + ((long)(bytesBigEndian[5]) << 16)
                + ((long)(bytesBigEndian[6]) << 8)
                + (long)(bytesBigEndian[7]);
        }

        public int ReadInt(bool optimizePositive)
        {
            int b = input.ReadByte();
            int result = b & 0x7F;
            if ((b & 0x80) != 0)
            {
                b = input.ReadByte();
                result |= (b & 0x7F) << 7;
                if ((b & 0x80) != 0)
                {
                    b = input.ReadByte();
                    result |= (b & 0x7F) << 14;
                    if ((b & 0x80) != 0)
                    {
                        b = input.ReadByte();
                        result |= (b & 0x7F) << 21;
                        if ((b & 0x80) != 0) result |= (input.ReadByte() & 0x7F) << 28;
                    }
                }
            }
            return optimizePositive ? result : ((result >> 1) ^ -(result & 1));
        }

        public string? ReadString()
        {
            try
            {
                int byteCount = ReadInt(true);
                if (byteCount == 0) return null;
                if (byteCount == 1) return "";
                
                byteCount--;
                
                // Ensure buffer is large enough
                byte[] buffer = this.chars;
                if (buffer.Length < byteCount)
                {
                    buffer = new byte[byteCount];
                }
                
                ReadFully(buffer, 0, byteCount);
                return System.Text.Encoding.UTF8.GetString(buffer, 0, byteCount);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in ReadString: {ex.Message}");
                return "";
            }
        }

        public string? ReadStringRef()
        {
            int index = ReadInt(true);
            return index == 0 ? null : strings[index - 1];
        }

        public void ReadFully(byte[] buffer, int offset, int length)
        {
            int bytesRead = 0;
            while (bytesRead < length)
            {
                int count = input.Read(buffer, offset + bytesRead, length - bytesRead);
                if (count <= 0) throw new EndOfStreamException();
                bytesRead += count;
            }
        }
    }

    // Data structures to hold skeleton data
    public class SkeletonData
    {
        public SkeletonInfo Skeleton { get; set; } = new SkeletonInfo();
        public List<BoneData> Bones { get; set; } = new List<BoneData>();
        public List<SlotData> Slots { get; set; } = new List<SlotData>();
        
        [JsonPropertyName("ik")]
        public List<IkConstraintData> Ik { get; set; } = new List<IkConstraintData>();
        
        // Add skin property - no separate DefaultSkin property
        public List<SkinData> Skins { get; set; } = new List<SkinData>();
        
        // Keep a reference to the default skin for internal use only
        [JsonIgnore]
        internal SkinData? defaultSkin;
    }
    
    public class SkeletonInfo
    {
        public string Hash { get; set; } = string.Empty;
        public string Spine { get; set; } = string.Empty;
    }

    public class BoneData
    {
        public string Name { get; set; } = string.Empty;
        public string? Parent { get; set; }
        public float? Length { get; set; }
        public float? X { get; set; }
        public float? Y { get; set; }
        public float? Rotation { get; set; }
        public float? ScaleX { get; set; }
        public float? ScaleY { get; set; }
        public float? ShearX { get; set; }
        public float? ShearY { get; set; }
        public string? Transform { get; set; }
        public bool? SkinRequired { get; set; }
    }
    
    public class SlotData
    {
        public string Name { get; set; } = string.Empty;
        public string Bone { get; set; } = string.Empty;
        public string? Color { get; set; }
        public string? Dark { get; set; }
        public string? Attachment { get; set; }
        public string? Blend { get; set; }
    }
    
    public class IkConstraintData
    {
        public string Name { get; set; } = string.Empty;
        public int? Order { get; set; }
        public bool? SkinRequired { get; set; }
        public List<string> Bones { get; set; } = new List<string>();
        public string Target { get; set; } = string.Empty;
        public float? Mix { get; set; }
        public float? Softness { get; set; }
        public int? BendDirection { get; set; }
        public bool? Compress { get; set; }
        public bool? Stretch { get; set; }
        public bool? Uniform { get; set; }
    }

    // Add the SkinData class
    public class SkinData
    {
        public string Name { get; set; } = string.Empty;
        public Dictionary<string, Dictionary<string, AttachmentData>> Attachments { get; set; } = new Dictionary<string, Dictionary<string, AttachmentData>>();
        
        // Only include bones and constraints if they have values
        private List<string> _bones = new List<string>();
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string> Bones { 
            get { return _bones.Count > 0 ? _bones : null; } 
            set { _bones = value ?? new List<string>(); }
        }
        
        private List<string> _constraints = new List<string>();
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string> Constraints { 
            get { return _constraints.Count > 0 ? _constraints : null; } 
            set { _constraints = value ?? new List<string>(); }
        }
    }

    public class AttachmentData
    {
        public string Type { get; set; } = string.Empty;
        public string? Path { get; set; }
        public string? Color { get; set; }
        
        // Region, Point attachment properties
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? Width { get; set; }
        
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? Height { get; set; }
        
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? Rotation { get; set; }
        
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? X { get; set; }
        
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? Y { get; set; }
        
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? ScaleX { get; set; }
        
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? ScaleY { get; set; }
        
        // Mesh, Path, BoundingBox properties
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float[]? Vertices { get; set; }
        
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? VertexCount { get; set; }
        
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int[]? Triangles { get; set; }
        
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float[]? Uvs { get; set; }
        
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? HullLength { get; set; }
        
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int[]? Edges { get; set; }
        
        // Path specific properties
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Closed { get; set; }
        
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? ConstantSpeed { get; set; }
        
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float[]? Lengths { get; set; }
        
        // Clipping specific properties
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? End { get; set; }
        
        // LinkedMesh specific properties
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Parent { get; set; }
        
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Skin { get; set; }
        
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? InheritTimeline { get; set; }
    }

    // Helper class for LinkedMeshInfo
    public class LinkedMeshInfo
    {
        public string ParentName { get; set; } = string.Empty;
        public string? SkinName { get; set; }
        public int SlotIndex { get; set; }
        public AttachmentData Mesh { get; set; }
        public bool InheritTimelines { get; set; }

        public LinkedMeshInfo(string? parentName, string? skinName, int slotIndex, AttachmentData? mesh, bool inheritTimelines)
        {
            ParentName = parentName ?? string.Empty;
            SkinName = skinName;
            SlotIndex = slotIndex;
            Mesh = mesh ?? new AttachmentData();
            InheritTimelines = inheritTimelines;
        }
    }
} 
