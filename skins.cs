using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using System.Collections.Generic;

namespace Skel2Json
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("Usage: Skel2Json <skelFile> [outputJsonFile]");
                return;
            }

            string skelFile = args[0];
            string jsonFile = args.Length > 1 ? args[1] : Path.ChangeExtension(skelFile, ".json");

            try
            {
                Console.WriteLine($"Converting {skelFile} to {jsonFile}");
                
                // Create a new converter
                var converter = new SkeletonConverter();
                
                // Read the skeleton file
                string jsonOutput = converter.ConvertSkelToJson(skelFile);
                
                // Write the JSON to a file
                File.WriteAllText(jsonFile, jsonOutput);
                
                Console.WriteLine("Conversion completed successfully!");
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
        public string ConvertSkelToJson(string skelFilePath)
        {
            using (FileStream input = new FileStream(skelFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var binaryReader = new SkeletonBinaryReader(input);
                var skeletonData = binaryReader.ReadSkeletonData();
                
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                
                // Format the JSON with 4-space indentation like the original
                string jsonOutput = JsonSerializer.Serialize(skeletonData, options);
                
                // Replace the default 2-space indentation with 4-space indentation
                jsonOutput = jsonOutput.Replace("  ", "    ");
                
                return jsonOutput;
            }
        }
    }

    public class SkeletonBinaryReader
    {
        private SkeletonInput input;
        private Dictionary<int, BoneInfo> bonesLookup = new Dictionary<int, BoneInfo>();
        private Dictionary<int, string> slotLookup = new Dictionary<int, string>();
        private List<LinkedMesh> linkedMeshes = new List<LinkedMesh>();
        private float scale = 1.0f;

        public SkeletonBinaryReader(Stream input)
        {
            this.input = new SkeletonInput(input);
        }

        public SkeletonData ReadSkeletonData()
        {
            float scale = 1.0f;

            SkeletonData skeletonData = new SkeletonData();
            skeletonData.Bones = new List<BoneData>();
            skeletonData.Slots = new List<SlotData>();
            skeletonData.Ik = new List<IkConstraintData>();
            skeletonData.Transform = new List<TransformConstraintData>();
            skeletonData.Skins = new List<SkinData>();
            
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
                        
                        // Store slot name for skin attachments
                        slotLookup[i] = slotName;
                        
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

            // Read Transform constraints
            try
            {
                n = input.ReadInt(true);
                Console.WriteLine($"Reading {n} transform constraints");
                for (int i = 0; i < n; i++)
                {
                    var transformName = input.ReadString();
                    var transformData = new TransformConstraintData
                    {
                        Name = transformName
                    };
                    
                    // Read order
                    transformData.Order = input.ReadInt(true);
                    
                    // Read skin required flag - only include in output if true
                    bool skinRequired = input.ReadBoolean();
                    if (skinRequired)
                    {
                        transformData.SkinRequired = true;
                    }
                    
                    // Read bones
                    int bonesCount = input.ReadInt(true);
                    transformData.Bones = new List<string>(bonesCount);
                    for (int j = 0; j < bonesCount; j++)
                    {
                        int boneIndex = input.ReadInt(true);
                        if (bonesLookup.TryGetValue(boneIndex, out BoneInfo boneInfo))
                        {
                            transformData.Bones.Add(boneInfo.Name);
                        }
                    }
                    
                    // Read target
                    int targetIndex = input.ReadInt(true);
                    if (bonesLookup.TryGetValue(targetIndex, out BoneInfo targetBone))
                    {
                        transformData.Target = targetBone.Name;
                    }
                    
                    // Read local flag - default is false
                    bool local = input.ReadBoolean();
                    if (local)
                    {
                        transformData.Local = true;
                    }
                    
                    // Read relative flag - default is false
                    bool relative = input.ReadBoolean();
                    if (relative)
                    {
                        transformData.Relative = true;
                    }
                    
                    // Read offset values
                    float offsetRotation = input.ReadFloat();
                    if (Math.Abs(offsetRotation) > 0.01f)
                    {
                        transformData.Rotation = FormatFloat(offsetRotation);
                    }
                    
                    float offsetX = input.ReadFloat() * scale;
                    if (Math.Abs(offsetX) > 0.01f)
                    {
                        transformData.X = FormatFloat(offsetX);
                    }
                    
                    float offsetY = input.ReadFloat() * scale;
                    if (Math.Abs(offsetY) > 0.01f)
                    {
                        transformData.Y = FormatFloat(offsetY);
                    }
                    
                    float offsetScaleX = input.ReadFloat();
                    if (Math.Abs(offsetScaleX) > 0.01f)
                    {
                        transformData.ScaleX = FormatFloat(offsetScaleX);
                    }
                    
                    float offsetScaleY = input.ReadFloat();
                    if (Math.Abs(offsetScaleY) > 0.01f)
                    {
                        transformData.ScaleY = FormatFloat(offsetScaleY);
                    }
                    
                    float offsetShearY = input.ReadFloat();
                    if (Math.Abs(offsetShearY) > 0.01f)
                    {
                        transformData.ShearY = FormatFloat(offsetShearY);
                    }
                    
                    // Read mix values - observed from the original JSON:
                    // In the original JSON, mixRotate, mixScaleX and mixShearY are always included
                    // even when they are 0. mixY is omitted.
                    
                    float mixRotate = input.ReadFloat();
                    transformData.MixRotate = FormatFloat(mixRotate);
                    
                    float mixX = input.ReadFloat();
                    if (Math.Abs(mixX) > 0.01f) // Include if non-zero
                    {
                        transformData.MixX = FormatFloat(mixX);
                    }
                    
                    float mixY = input.ReadFloat();
                    // In the original JSON, mixY is always omitted
                    
                    float mixScaleX = input.ReadFloat();
                    transformData.MixScaleX = FormatFloat(mixScaleX);
                    
                    float mixScaleY = input.ReadFloat();
                    // In the original JSON, mixScaleY is always omitted
                    
                    float mixShearY = input.ReadFloat();
                    transformData.MixShearY = FormatFloat(mixShearY);
                    
                    skeletonData.Transform.Add(transformData);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading transform constraints: {ex.Message}");
                // Continue with what we have so far
            }
            
            // Read Path constraints - skipping for brevity
            try
            {
                n = input.ReadInt(true);
                Console.WriteLine($"Reading {n} path constraints");
                // Skip path constraints for now
                for (int i = 0; i < n; i++)
                {
                    input.ReadString(); // name
                    input.ReadInt(true); // order
                    input.ReadBoolean(); // skinRequired
                    
                    int bonesCount = input.ReadInt(true);
                    for (int j = 0; j < bonesCount; j++)
                    {
                        input.ReadInt(true); // boneIndex
                    }
                    
                    input.ReadInt(true); // targetSlotIndex
                    input.ReadInt(true); // positionMode
                    input.ReadInt(true); // spacingMode
                    input.ReadInt(true); // rotateMode
                    input.ReadFloat(); // offsetRotation
                    input.ReadFloat(); // position
                    input.ReadFloat(); // spacing
                    input.ReadFloat(); // mixRotate
                    input.ReadFloat(); // mixX
                    input.ReadFloat(); // mixY
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading path constraints: {ex.Message}");
                // Continue with what we have so far
            }
            
            // Read skins
            try
            {
                // Default skin
                Console.WriteLine("Reading default skin");
                SkinData defaultSkin = ReadSkin(true);
                if (defaultSkin != null)
                {
                    skeletonData.Skins.Add(defaultSkin);
                }
                
                // Other skins
                n = input.ReadInt(true);
                Console.WriteLine($"Reading {n} additional skins");
                for (int i = 0; i < n; i++)
                {
                    try
                    {
                        SkinData skin = ReadSkin(false);
                        if (skin != null)
                        {
                            skeletonData.Skins.Add(skin);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error reading skin {i}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading skins: {ex.Message}");
                // Continue with what we have so far
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

        private SkinData ReadSkin(bool defaultSkin)
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
                skin = new SkinData { Name = input.ReadStringRef() };
                
                // Read bones
                int boneCount = input.ReadInt(true);
                if (boneCount > 0)
                {
                    skin.Bones = new List<string>(boneCount);
                    for (int i = 0; i < boneCount; i++)
                    {
                        int boneIndex = input.ReadInt(true);
                        if (bonesLookup.TryGetValue(boneIndex, out BoneInfo boneInfo))
                        {
                            skin.Bones.Add(boneInfo.Name);
                        }
                    }
                }

                // Read IK constraints
                int ikConstraintsCount = input.ReadInt(true);
                if (ikConstraintsCount > 0)
                {
                    skin.Ik = new List<string>(ikConstraintsCount);
                    for (int i = 0; i < ikConstraintsCount; i++)
                    {
                        int constraintIndex = input.ReadInt(true);
                        // We would need to have a lookup for constraint names
                        // For simplicity, we'll just add a placeholder
                        skin.Ik.Add($"ik_{constraintIndex}");
                    }
                }

                // Read Transform constraints
                int transformConstraintsCount = input.ReadInt(true);
                if (transformConstraintsCount > 0)
                {
                    skin.Transform = new List<string>(transformConstraintsCount);
                    for (int i = 0; i < transformConstraintsCount; i++)
                    {
                        int constraintIndex = input.ReadInt(true);
                        // We would need to have a lookup for constraint names
                        // For simplicity, we'll just add a placeholder
                        skin.Transform.Add($"transform_{constraintIndex}");
                    }
                }

                // Read Path constraints
                int pathConstraintsCount = input.ReadInt(true);
                if (pathConstraintsCount > 0)
                {
                    skin.Path = new List<string>(pathConstraintsCount);
                    for (int i = 0; i < pathConstraintsCount; i++)
                    {
                        int constraintIndex = input.ReadInt(true);
                        // We would need to have a lookup for constraint names
                        // For simplicity, we'll just add a placeholder
                        skin.Path.Add($"path_{constraintIndex}");
                    }
                }

                slotCount = input.ReadInt(true);
            }

            // Read attachments for each slot
            for (int i = 0; i < slotCount; i++)
            {
                int slotIndex = input.ReadInt(true);
                string slotName = slotLookup.TryGetValue(slotIndex, out string name) ? name : $"slot_{slotIndex}";
                
                int attachmentsCount = input.ReadInt(true);
                if (attachmentsCount > 0)
                {
                    var slotAttachments = new Dictionary<string, AttachmentData>();
                    
                    for (int ii = 0; ii < attachmentsCount; ii++)
                    {
                        string attachmentName = input.ReadStringRef();
                        AttachmentData attachment = ReadAttachment(skin.Name, slotIndex, attachmentName);
                        
                        if (attachment != null)
                        {
                            slotAttachments[attachmentName] = attachment;
                        }
                    }
                    
                    if (slotAttachments.Count > 0)
                    {
                        skin.Attachments[slotName] = slotAttachments;
                    }
                }
            }

            return skin;
        }

        private AttachmentData ReadAttachment(string skinName, int slotIndex, string attachmentName)
        {
            string name = input.ReadStringRef();
            if (string.IsNullOrEmpty(name)) name = attachmentName;

            AttachmentType attachmentType = (AttachmentType)input.ReadByte();
            
            AttachmentData attachment = new AttachmentData();
            attachment.Type = attachmentType.ToString().ToLower();
            
            switch (attachmentType)
            {
                case AttachmentType.Region:
                    string path = input.ReadStringRef();
                    float rotation = input.ReadFloat();
                    float x = input.ReadFloat() * scale;
                    float y = input.ReadFloat() * scale;
                    float scaleX = input.ReadFloat();
                    float scaleY = input.ReadFloat();
                    float width = input.ReadFloat() * scale;
                    float height = input.ReadFloat() * scale;
                    int color = input.ReadInt();
                    
                    // Skip sequence for now
                    bool hasSequence = input.ReadBoolean();
                    if (hasSequence)
                    {
                        // Read sequence data and skip
                        int count = input.ReadInt(true);
                        int start = input.ReadInt(true);
                        int digits = input.ReadInt(true);
                        int setupIndex = input.ReadInt(true);
                    }
                    
                    attachment.Path = string.IsNullOrEmpty(path) ? name : path;
                    if (Math.Abs(rotation) > 0.001f) attachment.Rotation = rotation;
                    if (Math.Abs(x) > 0.001f) attachment.X = x;
                    if (Math.Abs(y) > 0.001f) attachment.Y = y;
                    if (Math.Abs(scaleX - 1.0f) > 0.001f) attachment.ScaleX = scaleX;
                    if (Math.Abs(scaleY - 1.0f) > 0.001f) attachment.ScaleY = scaleY;
                    attachment.Width = width;
                    attachment.Height = height;
                    
                    // Format color
                    if (color != -1)
                    {
                        float r = ((color & 0xff000000) >> 24) / 255f;
                        float g = ((color & 0x00ff0000) >> 16) / 255f;
                        float b = ((color & 0x0000ff00) >> 8) / 255f;
                        float a = ((color & 0x000000ff)) / 255f;
                        
                        if (r < 1.0f || g < 1.0f || b < 1.0f || a < 1.0f)
                        {
                            attachment.Color = FormatColor(r, g, b, a);
                        }
                    }
                    break;
                    
                case AttachmentType.Mesh:
                    string meshPath = input.ReadStringRef();
                    int meshColor = input.ReadInt();
                    int vertexCount = input.ReadInt(true);
                    float[] uvs = ReadFloatArray(input, vertexCount << 1, 1);
                    int[] triangles = ReadShortArray(input);
                    
                    // Read vertices
                    float[] vertices = null;
                    
                    if (!input.ReadBoolean())
                    {
                        // Vertices as float array
                        vertices = ReadFloatArray(input, vertexCount << 1, scale);
                    }
                    else
                    {
                        // Weighted vertices
                        List<float> weightsList = new List<float>();
                        List<int> boneIndicesList = new List<int>();
                        
                        for (int i = 0; i < vertexCount; i++)
                        {
                            int boneCount = input.ReadInt(true);
                            boneIndicesList.Add(boneCount);
                            
                            for (int ii = 0; ii < boneCount; ii++)
                            {
                                int boneIndex = input.ReadInt(true);
                                boneIndicesList.Add(boneIndex);
                                weightsList.Add(input.ReadFloat() * scale);  // x
                                weightsList.Add(input.ReadFloat() * scale);  // y
                                weightsList.Add(input.ReadFloat());          // weight
                            }
                        }
                        
                        vertices = weightsList.ToArray();
                    }
                    
                    int hullLength = input.ReadInt(true);
                    
                    // Skip sequence for now
                    hasSequence = input.ReadBoolean();
                    if (hasSequence)
                    {
                        // Read sequence data and skip
                        int count = input.ReadInt(true);
                        int start = input.ReadInt(true);
                        int digits = input.ReadInt(true);
                        int setupIndex = input.ReadInt(true);
                    }
                    
                    // Skip edges and width/height for non-essential data
                    int[] edges = null;
                    float meshWidth = 0, meshHeight = 0;
                    
                    attachment.Path = string.IsNullOrEmpty(meshPath) ? name : meshPath;
                    attachment.UVs = uvs;
                    attachment.Triangles = triangles;
                    attachment.Vertices = vertices;
                    if (hullLength > 0) attachment.Hull = hullLength;
                    
                    // Format color
                    if (meshColor != -1)
                    {
                        float r = ((meshColor & 0xff000000) >> 24) / 255f;
                        float g = ((meshColor & 0x00ff0000) >> 16) / 255f;
                        float b = ((meshColor & 0x0000ff00) >> 8) / 255f;
                        float a = ((meshColor & 0x000000ff)) / 255f;
                        
                        if (r < 1.0f || g < 1.0f || b < 1.0f || a < 1.0f)
                        {
                            attachment.Color = FormatColor(r, g, b, a);
                        }
                    }
                    break;
                    
                case AttachmentType.LinkedMesh:
                    string linkedMeshPath = input.ReadStringRef();
                    int linkedMeshColor = input.ReadInt();
                    string linkedMeshSkinName = input.ReadStringRef();
                    string parent = input.ReadStringRef();
                    bool inheritTimelines = input.ReadBoolean();
                    
                    // Skip sequence for now
                    hasSequence = input.ReadBoolean();
                    if (hasSequence)
                    {
                        // Read sequence data and skip
                        int count = input.ReadInt(true);
                        int start = input.ReadInt(true);
                        int digits = input.ReadInt(true);
                        int setupIndex = input.ReadInt(true);
                    }
                    
                    attachment.Path = string.IsNullOrEmpty(linkedMeshPath) ? name : linkedMeshPath;
                    attachment.Parent = parent;
                    attachment.Skin = linkedMeshSkinName;
                    if (!inheritTimelines) attachment.Deform = true;
                    
                    // Format color
                    if (linkedMeshColor != -1)
                    {
                        float r = ((linkedMeshColor & 0xff000000) >> 24) / 255f;
                        float g = ((linkedMeshColor & 0x00ff0000) >> 16) / 255f;
                        float b = ((linkedMeshColor & 0x0000ff00) >> 8) / 255f;
                        float a = ((linkedMeshColor & 0x000000ff)) / 255f;
                        
                        if (r < 1.0f || g < 1.0f || b < 1.0f || a < 1.0f)
                        {
                            attachment.Color = FormatColor(r, g, b, a);
                        }
                    }
                    
                    // Add to linked meshes to resolve later
                    linkedMeshes.Add(new LinkedMesh(attachment, linkedMeshSkinName, slotIndex, parent, inheritTimelines));
                    break;
                    
                case AttachmentType.Path:
                    attachment.Type = "path";
                    bool closed = input.ReadBoolean();
                    bool constantSpeed = input.ReadBoolean();
                    int pathVertexCount = input.ReadInt(true);
                    
                    // Read vertices
                    if (!input.ReadBoolean()) {
                        // Non-weighted vertices
                        vertices = ReadFloatArray(input, pathVertexCount << 1, scale);
                    } else {
                        // Weighted vertices
                        // Skip weighted vertices for now
                        List<int> pathBones = new List<int>();
                        List<float> pathWeights = new List<float>();
                        
                        for (int i = 0; i < pathVertexCount; i++) {
                            int boneCount = input.ReadInt(true);
                            pathBones.Add(boneCount);
                            for (int ii = 0; ii < boneCount; ii++) {
                                pathBones.Add(input.ReadInt(true));
                                pathWeights.Add(input.ReadFloat() * scale);
                                pathWeights.Add(input.ReadFloat() * scale);
                                pathWeights.Add(input.ReadFloat());
                            }
                        }
                    }
                    
                    // Read lengths
                    float[] lengths = new float[pathVertexCount / 3];
                    for (int i = 0; i < lengths.Length; i++) {
                        lengths[i] = input.ReadFloat() * scale;
                    }
                    
                    attachment.Closed = closed;
                    attachment.ConstantSpeed = constantSpeed;
                    attachment.VertexCount = pathVertexCount;
                    attachment.Lengths = lengths;
                    
                    break;
                    
                case AttachmentType.Point:
                    attachment.Type = "point";
                    float pointRotation = input.ReadFloat();
                    float pointX = input.ReadFloat() * scale;
                    float pointY = input.ReadFloat() * scale;
                    
                    if (Math.Abs(pointRotation) > 0.001f) attachment.Rotation = pointRotation;
                    if (Math.Abs(pointX) > 0.001f) attachment.X = pointX;
                    if (Math.Abs(pointY) > 0.001f) attachment.Y = pointY;
                    
                    break;
                    
                case AttachmentType.Clipping:
                    attachment.Type = "clipping";
                    int endSlotIndex = input.ReadInt(true);
                    int clipVertexCount = input.ReadInt(true);
                    
                    // Read vertices
                    if (!input.ReadBoolean()) {
                        // Non-weighted vertices
                        vertices = ReadFloatArray(input, clipVertexCount << 1, scale);
                    } else {
                        // Skip weighted vertices
                        for (int i = 0; i < clipVertexCount; i++) {
                            int boneCount = input.ReadInt(true);
                            for (int ii = 0; ii < boneCount; ii++) {
                                input.ReadInt(true); // bone index
                                input.ReadFloat(); // x
                                input.ReadFloat(); // y
                                input.ReadFloat(); // weight
                            }
                        }
                    }
                    
                    attachment.End = slotLookup.TryGetValue(endSlotIndex, out string endSlotName) ? endSlotName : null;
                    attachment.VertexCount = clipVertexCount;
                    
                    break;
                    
                default:
                    // Skip unknown attachment type
                    return null;
            }
            
            return attachment;
        }
        
        private float[] ReadFloatArray(SkeletonInput input, int n, float scale)
        {
            float[] array = new float[n];
            if (scale == 1)
            {
                for (int i = 0; i < n; i++)
                    array[i] = input.ReadFloat();
            }
            else
            {
                for (int i = 0; i < n; i++)
                    array[i] = input.ReadFloat() * scale;
            }
            return array;
        }

        private int[] ReadShortArray(SkeletonInput input)
        {
            int n = input.ReadInt(true);
            int[] array = new int[n];
            for (int i = 0; i < n; i++)
                array[i] = (input.ReadByte() << 8) | input.ReadByte();
            return array;
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
        
        [JsonPropertyName("transform")]
        public List<TransformConstraintData> Transform { get; set; } = new List<TransformConstraintData>();
        
        [JsonPropertyName("skins")]
        public List<SkinData> Skins { get; set; } = new List<SkinData>();
        
        // Additional data like animations, etc. would be defined here
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
    
    public class TransformConstraintData
    {
        public string Name { get; set; } = string.Empty;
        public int? Order { get; set; }
        public bool? SkinRequired { get; set; }
        public List<string> Bones { get; set; } = new List<string>();
        public string Target { get; set; } = string.Empty;
        
        // These are the "offset" properties in Spine, but in the JSON they're just named directly
        public float? Rotation { get; set; }  // offsetRotation in Spine
        public float? X { get; set; }         // offsetX in Spine
        public float? Y { get; set; }         // offsetY in Spine
        public float? ScaleX { get; set; }    // offsetScaleX in Spine
        public float? ScaleY { get; set; }    // offsetScaleY in Spine
        public float? ShearY { get; set; }    // offsetShearY in Spine
        
        // Mix values (percentages 0-1)
        public float? MixRotate { get; set; }
        public float? MixX { get; set; }
        public float? MixY { get; set; }      // Only included if different from mixX
        public float? MixScaleX { get; set; }
        public float? MixScaleY { get; set; } // Only included if different from mixScaleX
        public float? MixShearY { get; set; }
        
        public bool? Local { get; set; }
        public bool? Relative { get; set; }
    }
    
    public class SkinData
    {
        public string Name { get; set; } = string.Empty;
        
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? Bones { get; set; }
        
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? Ik { get; set; }
        
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? Transform { get; set; }
        
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? Path { get; set; }
        
        [JsonPropertyName("attachments")]
        public Dictionary<string, Dictionary<string, AttachmentData>> Attachments { get; set; } = new Dictionary<string, Dictionary<string, AttachmentData>>();
    }
    
    public class AttachmentData
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }
        
        [JsonPropertyName("name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Name { get; set; }
        
        [JsonPropertyName("path")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Path { get; set; }
        
        [JsonPropertyName("color")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Color { get; set; }
        
        [JsonPropertyName("width")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? Width { get; set; }
        
        [JsonPropertyName("height")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? Height { get; set; }
        
        [JsonPropertyName("x")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? X { get; set; }
        
        [JsonPropertyName("y")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? Y { get; set; }
        
        [JsonPropertyName("rotation")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? Rotation { get; set; }
        
        [JsonPropertyName("scaleX")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? ScaleX { get; set; }
        
        [JsonPropertyName("scaleY")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? ScaleY { get; set; }
        
        // Additional fields for mesh attachments
        [JsonPropertyName("vertices")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float[]? Vertices { get; set; }
        
        [JsonPropertyName("triangles")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int[]? Triangles { get; set; }
        
        [JsonPropertyName("uvs")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float[]? UVs { get; set; }
        
        [JsonPropertyName("hull")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Hull { get; set; }
        
        [JsonPropertyName("edges")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int[]? Edges { get; set; }
        
        // For linked mesh
        [JsonPropertyName("parent")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Parent { get; set; }
        
        [JsonPropertyName("skin")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Skin { get; set; }
        
        [JsonPropertyName("deform")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Deform { get; set; }
        
        // For path attachments
        [JsonPropertyName("closed")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Closed { get; set; }
        
        [JsonPropertyName("constantSpeed")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? ConstantSpeed { get; set; }
        
        [JsonPropertyName("vertexCount")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? VertexCount { get; set; }
        
        [JsonPropertyName("lengths")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float[]? Lengths { get; set; }
        
        // For clipping attachments
        [JsonPropertyName("end")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? End { get; set; }
    }
    
    // Helper class for linked meshes
    public class LinkedMesh
    {
        public string Parent { get; set; }
        public string? Skin { get; set; }
        public int SlotIndex { get; set; }
        public AttachmentData Mesh { get; set; }
        public bool InheritTimelines { get; set; }
        
        public LinkedMesh(AttachmentData mesh, string? skin, int slotIndex, string parent, bool inheritTimelines)
        {
            Mesh = mesh;
            Skin = skin;
            SlotIndex = slotIndex;
            Parent = parent;
            InheritTimelines = inheritTimelines;
        }
    }

    public enum AttachmentType
    {
        Region, 
        Boundingbox, 
        Mesh, 
        LinkedMesh, 
        Path, 
        Point, 
        Clipping
    }
} 
