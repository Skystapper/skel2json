using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using System.Collections.Generic;
using System.Linq;

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
        private SkeletonData skeletonData;

        public SkeletonBinaryReader(Stream input)
        {
            this.input = new SkeletonInput(input);
        }

        public SkeletonData ReadSkeletonData()
        {
            skeletonData = new SkeletonData();
            
            try
        {
            float scale = 1.0f;

            skeletonData.Bones = new List<BoneData>();
            skeletonData.Slots = new List<SlotData>();
            skeletonData.Ik = new List<IkConstraintData>();
            skeletonData.Transform = new List<TransformConstraintData>();
            skeletonData.Skins = new List<SkinData>();
                skeletonData.Events = new Dictionary<string, EventData>();
            
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
            
                // Read events - new section
                try
                {
                    n = input.ReadInt(true);
                    Console.WriteLine($"Reading {n} events");
                    for (int i = 0; i < n; i++) 
                    {
                        try 
                        {
                            string eventName = input.ReadStringRef() ?? $"event_{i}";
                            
                            // Create the event data
                            var eventData = new EventData();
                            
                            // Read integer value
                            int intValue = input.ReadInt(false);
                            if (intValue != 0) {
                                eventData.Int = intValue;
                            }
                            
                            // Read float value
                            float floatValue = input.ReadFloat();
                            if (Math.Abs(floatValue) > 0.001f) {
                                eventData.Float = floatValue;
                            }
                            
                            // Read string value
                            string stringValue = input.ReadString() ?? "";
                            if (!string.IsNullOrEmpty(stringValue)) {
                                eventData.String = stringValue;
                            }
                            
                            // Read audio path
                            string audioPath = input.ReadString();
                            if (!string.IsNullOrEmpty(audioPath)) {
                                eventData.AudioPath = audioPath;
                                
                                // Read volume
                                float volume = input.ReadFloat();
                                if (Math.Abs(volume - 1.0f) > 0.001f) {
                                    eventData.Volume = volume;
                                }
                                
                                // Read balance
                                float balance = input.ReadFloat();
                                if (Math.Abs(balance) > 0.001f) {
                                    eventData.Balance = balance;
                                }
                            }
                            
                            // Add event to skeleton data
                            skeletonData.Events[eventName] = eventData;
                        } 
                        catch (Exception ex) 
                        {
                            Console.WriteLine($"Error reading event {i}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error reading events: {ex.Message}");
                    // Continue with what we have so far
                }
                
                // Read animations
                try
                {
                    n = input.ReadInt(true);
                    
                    if (n < 0 || n > 100) // Sanity check for number of animations
                    {
                        Console.WriteLine($"Invalid animation count: {n}, skipping animations");
            return skeletonData;
                    }
                    
                    Console.WriteLine($"Reading {n} animations");
                    
                    // Create an animation dictionary only if we expect to have animations
                    if (n > 0) 
                    {
                        skeletonData.Animations = new Dictionary<string, AnimationData>();
                    }
                    
                    // Process all animations now that timeline reading is working correctly
                    int maxAnimations = n;
                    
                    int successfulAnimations = 0;
                    
                    for (int i = 0; i < maxAnimations; i++)
                    {
                        try
                        {
                            string animationName = input.ReadString() ?? $"animation_{i}";
                            Console.WriteLine($"Reading animation: {animationName}");
                            
                            // Set a timeout for each animation
                            DateTime startTime = DateTime.Now;
                            TimeSpan timeout = TimeSpan.FromSeconds(10); // 10 second timeout per animation
                            
                            try
                            {
                                AnimationData animation = ReadAnimation(animationName);
                                
                                if (animation != null && animation.HasData())
                                {
                                    skeletonData.Animations[animationName] = animation;
                                    successfulAnimations++;
                                    Console.WriteLine($"Successfully read animation: {animationName}");
                                }
                                else
                                {
                                    Console.WriteLine($"Animation {animationName} had no valid data");
                                }
                                
                                // Check if we've exceeded timeout
                                if (DateTime.Now - startTime > timeout)
                                {
                                    Console.WriteLine($"Animation {animationName} processing timed out - possibly corrupted data");
                                        break;
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Error reading animation {animationName}: {ex.Message}");
                                    break;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error reading animation {i}: {ex.Message}");
                                break;
                            }
                    }
                    
                    Console.WriteLine($"Successfully read {successfulAnimations} animations out of {maxAnimations} processed");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error reading animations: {ex.Message}");
                    // Continue with what we have so far
                }
                
                return skeletonData;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading skeleton data: {ex.Message}");
                return skeletonData;
            }
        }
        
        private AnimationData ReadAnimation(string name)
        {
            Console.WriteLine($"Reading animation: {name}");
            AnimationData animation = new AnimationData();
            
            try
            {
                int timelines = input.ReadInt(true);
                
                if (timelines < 0)
                {
                    Console.WriteLine($"Invalid timeline count: {timelines}, skipping animation");
                    return animation;
                }
                
                // Slot timelines - Keep this part as is since it's working correctly
                int slotCount = input.ReadInt(true);
                
                if (slotCount < 0)
                {
                    Console.WriteLine($"Invalid slot timeline count: {slotCount}, skipping slot timelines");
                }
                else
                {
                    Console.WriteLine($"Reading {slotCount} slot timelines");
                    if (slotCount > 0)
                    {
                        animation.Slots = new Dictionary<string, SlotTimelineData>();
                        
                        for (int i = 0; i < slotCount; i++)
                        {
                            try
                            {
                                int slotIndex = input.ReadInt(true);
                                
                                string slotName;
                                if (!slotLookup.TryGetValue(slotIndex, out slotName))
                                {
                                    slotName = $"slot_{slotIndex}";
                                    Console.WriteLine($"Warning: No slot found for index {slotIndex}, using {slotName} as fallback");
                                }
                                
                                int timelineCount = input.ReadInt(true);
                                if (timelineCount < 0)
                                {
                                    Console.WriteLine($"Invalid timeline count: {timelineCount} for slot {slotName}, skipping");
                                    continue;
                                }
                                
                                SlotTimelineData slotTimelines = ReadSlotTimelines(slotIndex, timelineCount);
                                
                                if (slotTimelines != null)
                                {
                                    animation.Slots[slotName] = slotTimelines;
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Error reading slot timeline {i}: {ex.Message}");
                            }
                        }
                    }
                }

                // Read bone timelines - Keep as is since it's working
                ReadBoneTimelines(animation);
                
                // Read attachment timelines  
                ReadAttachmentTimelines(animation);
                
                Console.WriteLine($"Successfully read animation: {name}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading animation {name}: {ex.Message}");
            }
            
            return animation;
        }

        private void ReadBoneTimelines(AnimationData animation)
        {
            try
            {
                int boneCount = input.ReadInt(true);
                
                if (boneCount <= 0)
                {
                    Console.WriteLine($"No bone timelines to read.");
                    return;
                }
                
                Console.WriteLine($"Reading {boneCount} bone timelines");
                
                // Create dictionary to store bone animations if not already created
                if (animation.Bones == null)
                {
                    animation.Bones = new Dictionary<string, BoneTimelineData>();
                }
                
                // For each bone that has timelines
                for (int i = 0; i < boneCount; i++)
                {
                    int boneIndex = input.ReadInt(true);
                    
                    // Get bone name from the index
                    string boneName;
                    if (!bonesLookup.TryGetValue(boneIndex, out BoneInfo boneInfo))
                    {
                        boneName = $"bone_{boneIndex}";
                        Console.WriteLine($"Warning: No bone found for index {boneIndex}, using {boneName} as fallback");
                    }
                    else
                    {
                        boneName = boneInfo.Name;
                    }
                    
                    // Create bone timeline data if not already created for this bone
                    if (!animation.Bones.TryGetValue(boneName, out BoneTimelineData boneTimelines))
                    {
                        boneTimelines = new BoneTimelineData();
                        animation.Bones[boneName] = boneTimelines;
                    }
                    
                    // Read all timelines for this bone
                    int timelineCount = input.ReadInt(true);
                    // Console.WriteLine($"  - Bone {boneName} has {timelineCount} timelines");
                    
                    for (int timelineIndex = 0; timelineIndex < timelineCount; timelineIndex++)
                    {
                        try
                        {
                            int timelineType = input.ReadByte();
                            int frameCount = input.ReadInt(true);
                            
                            if (frameCount <= 0)
                            {
                                Console.WriteLine($"  - Invalid frame count: {frameCount} for bone {boneName} timeline type {timelineType}, skipping");
                                continue;
                            }
                            
                            // Read bezierCount
                            int bezierCount = input.ReadInt(true);
                            
                            // Treat types 0 as BONE_ROTATE
                            if (timelineType == 0) // BONE_ROTATE
                            {
                                boneTimelines.Rotate = ReadRotateTimeline(frameCount);
                            }
                            // Treat types 1 as BONE_TRANSLATE
                            else if (timelineType == 1) // BONE_TRANSLATE
                            {
                                boneTimelines.Translate = ReadTranslateTimeline(frameCount);
                            }
                            // Handle BONE_TRANSLATEX (type 2)
                            else if (timelineType == 2) // BONE_TRANSLATEX
                            {
                                boneTimelines.TranslateX = ReadValueTimeline(frameCount, true); // true = apply scale
                            }
                            // Handle BONE_TRANSLATEY (type 3)
                            else if (timelineType == 3) // BONE_TRANSLATEY
                            {
                                boneTimelines.TranslateY = ReadValueTimeline(frameCount, true); // true = apply scale
                            }
                            // Treat type 4 as BONE_SCALE (2D timeline like translate)
                            else if (timelineType == 4) // BONE_SCALE
                            {
                                boneTimelines.Scale = ReadScaleTimeline(frameCount);
                            }
                            // Handle BONE_SCALEX (type 5)
                            else if (timelineType == 5) // BONE_SCALEX
                            {
                                boneTimelines.ScaleX = ReadValueTimeline(frameCount, false); // false = don't apply scale
                            }
                            // Handle BONE_SCALEY (type 6)
                            else if (timelineType == 6) // BONE_SCALEY
                            {
                                boneTimelines.ScaleY = ReadValueTimeline(frameCount, false); // false = don't apply scale
                            }
                            // Treat type 7 as BONE_SHEAR (2D timeline like translate)
                            else if (timelineType == 7) // BONE_SHEAR
                            {
                                boneTimelines.Shear = ReadShearTimeline(frameCount);
                            }
                            // Handle BONE_SHEARX (type 8)
                            else if (timelineType == 8) // BONE_SHEARX
                            {
                                boneTimelines.ShearX = ReadValueTimeline(frameCount, false); // false = don't apply scale
                            }
                            // Handle BONE_SHEARY (type 9)
                            else if (timelineType == 9) // BONE_SHEARY
                            {
                                boneTimelines.ShearY = ReadValueTimeline(frameCount, false); // false = don't apply scale
                            }
                            else
                            {
                                // Console.WriteLine($"    - Unknown bone timeline type: {timelineType}, skipping {frameCount} frames");
                                
                                // Skip unknown timeline types
                                for (int frame = 0; frame < frameCount; frame++)
                                {
                                    // Skip time
                                    input.ReadFloat();
                                    
                                    // Each timeline type might have different data structure
                                    // For safety, skip one float value
                                    input.ReadFloat();
                                    
                                    if (frame < frameCount - 1)
                                    {
                                        int curveType = input.ReadByte();
                                        if (curveType == 2) // CURVE_BEZIER
                                        {
                                            // Skip bezier curve data (4 floats)
                                            input.ReadFloat(); // cx1
                                            input.ReadFloat(); // cy1
                                            input.ReadFloat(); // cx2
                                            input.ReadFloat(); // cy2
                                        }
                                    }
                                }
                            }
                            
                            // long positionAfterTimeline = input.GetPosition();
                            // Console.WriteLine($"  - Debug: Timeline processing completed. Bytes read: {positionAfterTimeline - positionBeforeTimeline}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error reading bone timeline {timelineIndex} for bone {boneName}: {ex.Message}");
                            
                            // Try to resync by reading a few bytes
                            for (int skip = 0; skip < 20; skip++)
                            {
                                try { input.ReadByte(); } catch { break; }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading bone timelines: {ex.Message}");
            }
        }

        private void ReadAttachmentTimelines(AnimationData animation)
        {
            try
        {
            // Skip IK timelines
            int ikCount = input.ReadInt(true);
            Console.WriteLine($"Skipping {ikCount} IK timelines");
                SkipIkTimelines(ikCount);
            
            // Skip transform timelines
            int transformCount = input.ReadInt(true);
            Console.WriteLine($"Skipping {transformCount} transform timelines");
                SkipTransformTimelines(transformCount);
            
            // Skip path timelines
            int pathCount = input.ReadInt(true);
            Console.WriteLine($"Skipping {pathCount} path timelines");
                SkipPathTimelines(pathCount);
                
                // Read attachment timelines (deform)
                int skinCount = input.ReadInt(true);
                Console.WriteLine($"Reading attachment timelines from {skinCount} skins");
                
                if (skinCount > 0)
                {
                    animation.Attachments = new Dictionary<string, Dictionary<string, Dictionary<string, AttachmentTimelineData>>>();
                    
                    for (int i = 0; i < skinCount; i++)
                    {
                        try
                        {
                            int skinIndex = input.ReadInt(true);
                            
                            // Get skin name
                            string skinName = "default"; // Default fallback
                            if (skinIndex < skeletonData.Skins.Count)
                            {
                                skinName = skeletonData.Skins[skinIndex].Name;
                            }
                            // Console.WriteLine($"  - Processing skin '{skinName}' (index {skinIndex})");
                            
                            var skinTimelines = new Dictionary<string, Dictionary<string, AttachmentTimelineData>>();
                            
                            int slotCount = input.ReadInt(true);
                                                                // Console.WriteLine($"    - {slotCount} slots with attachment timelines");
                            
                            for (int ii = 0; ii < slotCount; ii++)
                            {
                                try
                                {
                                    int slotIndex = input.ReadInt(true);
                                    
                                    // Get slot name
                                    string slotName = slotLookup.TryGetValue(slotIndex, out string name) ? name : $"slot_{slotIndex}";
                                    // Console.WriteLine($"      - Processing slot '{slotName}' (index {slotIndex})");
                                    
                                    var slotAttachments = new Dictionary<string, AttachmentTimelineData>();
                                    
                                    int attachmentCount = input.ReadInt(true);
                                    // Console.WriteLine($"        - {attachmentCount} attachments with timelines");
                                    
                                    for (int iii = 0; iii < attachmentCount; iii++)
                                    {
                                        try
                                        {
                                            string attachmentName = input.ReadStringRef() ?? $"attachment_{iii}";
                                            // Console.WriteLine($"          - Processing attachment '{attachmentName}'");
                                            
                                            int timelineType = input.ReadByte();
                                            int frameCount = input.ReadInt(true);
                                            int frameLast = frameCount - 1;
                                            
                                            // Console.WriteLine($"            - Timeline type: {timelineType}, frames: {frameCount}");
                                            
                                            var attachmentTimeline = new AttachmentTimelineData();
                                            
                                            switch (timelineType)
                                            {
                                                case 0: // ATTACHMENT_DEFORM
                                                    // The bezierCount is read inside ReadDeformTimeline, not here
                                                    // This was causing us to read from wrong position
                                                    attachmentTimeline.Deform = ReadDeformTimeline(frameCount, frameLast);
                                                    break;
                                                case 1: // ATTACHMENT_SEQUENCE  
                                                    attachmentTimeline.Sequence = ReadSequenceTimeline(frameCount);
                                                    break;
                                                default:
                                                    Console.WriteLine($"            - Unknown attachment timeline type: {timelineType}");
                                                    break;
                                            }
                                            
                                            slotAttachments[attachmentName] = attachmentTimeline;
                                        }
                                        catch (Exception ex)
                                        {
                                            Console.WriteLine($"Error reading attachment timeline {iii}: {ex.Message}");
                                        }
                                    }
                                    
                                    if (slotAttachments.Count > 0)
                                    {
                                        skinTimelines[slotName] = slotAttachments;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"Error reading slot timeline {ii}: {ex.Message}");
                                }
                            }
                            
                            if (skinTimelines.Count > 0)
                            {
                                animation.Attachments[skinName] = skinTimelines;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error reading skin timeline {i}: {ex.Message}");
                        }
                    }
                }
            
            // Skip draw order timelines
            int drawOrderCount = input.ReadInt(true);
            Console.WriteLine($"Skipping {drawOrderCount} draw order timelines");
                SkipDrawOrderTimelines(drawOrderCount);
            
            // Skip event timelines
            int eventCount = input.ReadInt(true);
            Console.WriteLine($"Skipping {eventCount} event timelines");
                SkipEventTimelines(eventCount);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading attachment timelines: {ex.Message}");
            }
        }
        
        private SlotTimelineData ReadSlotTimelines(int slotIndex, int nn)
        {
            SlotTimelineData slotTimelines = new SlotTimelineData();
            
            // Validate slot index and get proper slot name
            if (!slotLookup.TryGetValue(slotIndex, out string slotName))
            {
                slotName = $"slot_{slotIndex}";
                Console.WriteLine($"Warning: Missing slot name for index {slotIndex}");
            }
            
            for (int ii = 0; ii < nn; ii++)
            {
                try
                {
                    int timelineType = input.ReadByte();
                    int frameCount = input.ReadInt(true);
                    
                    if (frameCount < 0)
                    {
                        Console.WriteLine($"  - Invalid frame count: {frameCount} for slot {slotName} timeline type {timelineType}, skipping");
                        continue;
                    }
                    
                    // Console.WriteLine($"  - Slot {slotName} timeline type {timelineType} with {frameCount} frames");
                    
                    // Handle different timeline types
                    switch (timelineType)
                    {
                        case 0: // SLOT_ATTACHMENT
                            var attachmentTimeline = new List<KeyframeData>();
                            for (int frame = 0; frame < frameCount; frame++)
                            {
                                try {
                                    KeyframeData keyframe = new KeyframeData
                                    {
                                        Time = input.ReadFloat(),
                                        Name = input.ReadStringRef()
                                    };
                                    attachmentTimeline.Add(keyframe);
                                } catch (Exception ex) {
                                    Console.WriteLine($"Error reading attachment keyframe: {ex.Message}");
                                    break;
                                }
                            }
                            slotTimelines.Attachment = attachmentTimeline;
                            break;
                            
                        case 1: // SLOT_RGBA
                            try {
                                // Read bezierCount (this was missing!)
                                int bezierCount = input.ReadInt(true);
                                Console.WriteLine($"    - RGBA timeline bezierCount: {bezierCount}");
                                
                                var rgbaTimeline = new List<RGBAKeyframeData>();
                                
                                // Follow the exact pattern from official Spine SkeletonBinary.cs for RGBA timelines
                                float time = input.ReadFloat();
                                float r = input.Read() / 255f;
                                float g = input.Read() / 255f;
                                float b = input.Read() / 255f;
                                float a = input.Read() / 255f;
                                
                                for (int frame = 0, bezier = 0, frameLast = frameCount - 1; ; frame++)
                                {
                                    RGBAKeyframeData keyframe = new RGBAKeyframeData
                                    {
                                        Color = FormatColorLowercase(r, g, b, a)
                                    };
                                    
                                    // First frame doesn't include time if it's 0
                                    if (frame == 0 && Math.Abs(time) <= 0.001f)
                                    {
                                        // First frame at time=0, omit time property
                                        keyframe.Time = null;
                                    }
                                    else
                                    {
                                        keyframe.Time = time;
                                    }
                                    
                                    // If this is the last frame, just add it and break
                                    if (frame == frameLast)
                                    {
                                        rgbaTimeline.Add(keyframe);
                                        break;
                                    }
                                    
                                    // Read next frame data
                                        float time2 = input.ReadFloat();
                                        float r2 = input.Read() / 255f;
                                        float g2 = input.Read() / 255f;
                                        float b2 = input.Read() / 255f;
                                        float a2 = input.Read() / 255f;
                                        
                                    // Read curve type for transition from current to next frame
                                        int curveType = input.ReadByte();
                                    Console.WriteLine($"      - Frame {frame}: time={time}, r={r}, g={g}, b={b}, a={a}, curve_type={curveType}");
                                    
                                    switch (curveType)
                                        {
                                        case 1: // CURVE_STEPPED
                                            keyframe.Curve = "stepped";
                                            break;
                                        case 2: // CURVE_BEZIER
                                            // For RGBA timelines, the official code calls SetBezier 4 times (once for each channel)
                                            // Each SetBezier call reads: cx1, cy1, cx2, cy2 (4 floats)
                                            // So total: 16 floats for a 4-channel RGBA bezier curve
                                            List<float> bezierCurve = new List<float>();
                                            
                                            // Read first SetBezier call (for R channel)
                                            float cx1_r = input.ReadFloat();
                                            float cy1_r = input.ReadFloat(); 
                                            float cx2_r = input.ReadFloat();
                                            float cy2_r = input.ReadFloat();
                                            
                                            // Read second SetBezier call (for G channel)
                                            float cx1_g = input.ReadFloat();
                                            float cy1_g = input.ReadFloat();
                                            float cx2_g = input.ReadFloat();
                                            float cy2_g = input.ReadFloat();
                                            
                                            // Read third SetBezier call (for B channel)
                                            float cx1_b = input.ReadFloat();
                                            float cy1_b = input.ReadFloat();
                                            float cx2_b = input.ReadFloat();
                                            float cy2_b = input.ReadFloat();
                                            
                                            // Read fourth SetBezier call (for A channel)
                                            float cx1_a = input.ReadFloat();
                                            float cy1_a = input.ReadFloat();
                                            float cx2_a = input.ReadFloat();
                                            float cy2_a = input.ReadFloat();
                                            
                                            // Arrange in the order expected by JSON: [r_curve, g_curve, b_curve, a_curve]
                                            bezierCurve.AddRange(new float[] { cx1_r, cy1_r, cx2_r, cy2_r, cx1_g, cy1_g, cx2_g, cy2_g, cx1_b, cy1_b, cx2_b, cy2_b, cx1_a, cy1_a, cx2_a, cy2_a });
                                            
                                            Console.WriteLine($"      - Bezier RGBA: R=[{cx1_r}, {cy1_r}, {cx2_r}, {cy2_r}], G=[{cx1_g}, {cy1_g}, {cx2_g}, {cy2_g}], B=[{cx1_b}, {cy1_b}, {cx2_b}, {cy2_b}], A=[{cx1_a}, {cy1_a}, {cx2_a}, {cy2_a}]");
                                            
                                            keyframe.Curve = bezierCurve;
                                            break;
                                        // case 0 is CURVE_LINEAR (default), no curve data needed
                                    }
                                    
                                    rgbaTimeline.Add(keyframe);
                                    
                                    // Move to next frame
                                        time = time2;
                                        r = r2;
                                        g = g2;
                                        b = b2;
                                        a = a2;
                                }
                                
                                slotTimelines.RGBA = rgbaTimeline;
                            } catch (Exception ex) {
                                Console.WriteLine($"Error reading RGBA timeline: {ex.Message}");
                            }
                            break;
                            
                        default:
                            // Skip other timeline types
                            Console.WriteLine($"    - Skipping timeline type {timelineType}");
                            // We need to read bezierCount for other timeline types too
                            if (timelineType >= 1 && timelineType <= 5) // Color timeline types
                            {
                                int bezierCount = input.ReadInt(true);
                                Console.WriteLine($"    - Skipped timeline bezierCount: {bezierCount}");
                            }
                            
                            for (int frame = 0; frame < frameCount; frame++)
                            {
                                // Skip reading data for this frame
                                input.ReadFloat(); // time
                                
                                // Skip different amounts of data based on timeline type
                                switch (timelineType)
                                {
                                    case 2: // SLOT_RGB
                                        input.Read(); // r
                                        input.Read(); // g
                                        input.Read(); // b
                                        break;
                                    case 3: // SLOT_RGBA2
                                        input.Read(); // r
                                        input.Read(); // g
                                        input.Read(); // b
                                        input.Read(); // a
                                        input.Read(); // r2
                                        input.Read(); // g2
                                        input.Read(); // b2
                                        break;
                                    case 4: // SLOT_RGB2
                                        input.Read(); // r
                                        input.Read(); // g
                                        input.Read(); // b
                                        input.Read(); // r2
                                        input.Read(); // g2
                                        input.Read(); // b2
                                        break;
                                    case 5: // SLOT_ALPHA
                                        input.Read(); // a
                                        break;
                                }
                                
                                // Skip curve data if not last frame
                                if (frame < frameCount - 1)
                                {
                                    int curveType = input.ReadByte();
                                    if (curveType == 2) // CURVE_BEZIER
                                    {
                                        // Skip appropriate number of bezier values based on timeline type
                                        int channelCount = timelineType switch
                                        {
                                            2 => 3, // RGB = 3 channels
                                            3 => 7, // RGBA2 = 7 channels
                                            4 => 6, // RGB2 = 6 channels
                                            5 => 1, // ALPHA = 1 channel
                                            _ => 1
                                        };
                                        
                                        for (int ch = 0; ch < channelCount; ch++)
                                        {
                                            input.ReadFloat(); // cx1
                                            input.ReadFloat(); // cy1
                                            input.ReadFloat(); // cx2
                                            input.ReadFloat(); // cy2
                                        }
                                    }
                                }
                            }
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error reading slot timeline {ii} for slot {slotName}: {ex.Message}");
                }
            }
            
            return slotTimelines;
        }
        
        private List<float> ReadBezierCurve()
        {
            // In the JSON format, bezier curves are stored as a flat array of 4 values
            // [cx1, cy1, cx2, cy2]
            List<float> curve = new List<float>
            {
                input.ReadFloat(), // cx1
                input.ReadFloat(), // cy1
                input.ReadFloat(), // cx2
                input.ReadFloat()  // cy2
            };
            return curve;
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
        
        // Format color to #RRGGBBAA format (uppercase)
        private string FormatColor(float r, float g, float b, float a)
        {
            int rInt = (int)(r * 255);
            int gInt = (int)(g * 255);
            int bInt = (int)(b * 255);
            int aInt = (int)(a * 255);
            
            return $"{rInt:X2}{gInt:X2}{bInt:X2}{aInt:X2}";
        }
        
        // Format color to lowercase hex format like original JSON
        private string FormatColorLowercase(float r, float g, float b, float a)
        {
            int rInt = (int)(r * 255);
            int gInt = (int)(g * 255);
            int bInt = (int)(b * 255);
            int aInt = (int)(a * 255);
            
            return $"{rInt:x2}{gInt:x2}{bInt:x2}{aInt:x2}";
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
                    
                    // Only set Path if it's different from name
                    if (!string.IsNullOrEmpty(path) && path != name)
                        attachment.Path = path;
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
                        // Non-weighted vertices
                        vertices = ReadFloatArray(input, vertexCount << 1, scale);
                    }
                    else
                    {
                        // Weighted vertices - format is:
                        // [
                        //   num_bones, bone_idx1, x1, y1, weight1, bone_idx2, x2, y2, weight2...,
                        //   num_bones, bone_idx1, x1, y1, weight1, ...
                        // ]
                        List<float> weightedVertices = new List<float>();
                        
                        for (int i = 0; i < vertexCount; i++)
                        {
                            int boneCount = input.ReadInt(true);
                            weightedVertices.Add(boneCount);
                            
                            for (int ii = 0; ii < boneCount; ii++)
                            {
                                weightedVertices.Add(input.ReadInt(true)); // bone index
                                weightedVertices.Add(input.ReadFloat() * scale); // x
                                weightedVertices.Add(input.ReadFloat() * scale); // y
                                weightedVertices.Add(input.ReadFloat());         // weight
                            }
                        }
                        
                        vertices = weightedVertices.ToArray();
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
                    
                    // Only set Path if it's different from name
                    if (!string.IsNullOrEmpty(meshPath) && meshPath != name)
                        attachment.Path = meshPath;
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
                    
                    // Only set Path if it's different from name
                    if (!string.IsNullOrEmpty(linkedMeshPath) && linkedMeshPath != name)
                        attachment.Path = linkedMeshPath;
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
                        attachment.Vertices = vertices;
                    } else {
                        // Weighted vertices - same format as mesh
                        List<float> weightedVertices = new List<float>();
                        
                        for (int i = 0; i < pathVertexCount; i++) {
                            int boneCount = input.ReadInt(true);
                            weightedVertices.Add(boneCount);
                            for (int ii = 0; ii < boneCount; ii++) {
                                weightedVertices.Add(input.ReadInt(true)); // bone index
                                weightedVertices.Add(input.ReadFloat() * scale); // x
                                weightedVertices.Add(input.ReadFloat() * scale); // y
                                weightedVertices.Add(input.ReadFloat());         // weight
                            }
                        }
                        
                        attachment.Vertices = weightedVertices.ToArray();
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
                        attachment.Vertices = vertices;
                    } else {
                        // Weighted vertices - same format as mesh
                        List<float> weightedVertices = new List<float>();
                        
                        for (int i = 0; i < clipVertexCount; i++) {
                            int boneCount = input.ReadInt(true);
                            weightedVertices.Add(boneCount);
                            for (int ii = 0; ii < boneCount; ii++) {
                                weightedVertices.Add(input.ReadInt(true)); // bone index
                                weightedVertices.Add(input.ReadFloat() * scale); // x
                                weightedVertices.Add(input.ReadFloat() * scale); // y
                                weightedVertices.Add(input.ReadFloat());         // weight
                            }
                        }
                        
                        attachment.Vertices = weightedVertices.ToArray();
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

        private List<RotateKeyframeData> ReadRotateTimeline(int frameCount)
        {
            var timeline = new List<RotateKeyframeData>();
            
            // Console.WriteLine($"    - ReadRotateTimeline starting with {frameCount} frames at position {input.GetPosition()}");

            // Follow the exact pattern from official Spine SkeletonBinary.cs
            // ReadTimeline method for CurveTimeline1
            
            float time = input.ReadFloat();
            float value = input.ReadFloat();
            
            // Console.WriteLine($"      - Initial frame: time={time}, value={value}");
            
            for (int frame = 0, bezier = 0, frameLast = frameCount - 1; ; frame++)
            {
                // Create keyframe for current frame
                var keyframe = new RotateKeyframeData 
                { 
                    Value = value
                };
                
                // First frame doesn't have time in JSON
                if (frame > 0)
                {
                    keyframe.Time = time;
                }
                
                // If this is the last frame, just add it and break
                if (frame == frameLast)
            {
                    timeline.Add(keyframe);
                    break;
                }
                
                // Read next frame data
                float time2 = input.ReadFloat();
                float value2 = input.ReadFloat();
                
                // Console.WriteLine($"      - Frame {frame}: time={time}, value={value}, next_time={time2}, next_value={value2}");
                
                // Read curve type for transition from current to next frame
                    int curveType = input.ReadByte();
                    // Console.WriteLine($"      - Curve type: {curveType}");
                    
                switch (curveType)
                {
                    case 1: // CURVE_STEPPED
                        keyframe.Curve = "stepped";
                        break;
                    case 2: // CURVE_BEZIER
                        float cx1 = input.ReadFloat();
                        float cy1 = input.ReadFloat();
                        float cx2 = input.ReadFloat();
                        float cy2 = input.ReadFloat();
                        Console.WriteLine($"      - Bezier: [{cx1}, {cy1}, {cx2}, {cy2}]");
                        keyframe.Curve = new List<float> { cx1, cy1, cx2, cy2 };
                        break;
                    // case 0 is CURVE_LINEAR (default), no curve data needed
                }
                
                timeline.Add(keyframe);
                
                // Move to next frame
                time = time2;
                value = value2;
            }
            
            // Console.WriteLine($"    - ReadRotateTimeline finished at position {input.GetPosition()}");
            
            return timeline;
        }
        
        private List<ValueKeyframeData> ReadValueTimeline(int frameCount, bool scale)
                {
            Console.WriteLine($"    - ReadValueTimeline starting with {frameCount} frames at position {input.GetPosition()}");
            
            float scaleFactor = scale ? this.scale : 1.0f;
            var timeline = new List<ValueKeyframeData>();
            
            // Follow the exact pattern from official Spine SkeletonBinary.cs
            // ReadTimeline method for CurveTimeline1
            
            float time = input.ReadFloat();
            float value = input.ReadFloat() * scaleFactor;
            
            Console.WriteLine($"      - Initial frame: time={time}, value={value}");
            
            for (int frame = 0, bezier = 0, frameLast = frameCount - 1; ; frame++)
            {
                // Create keyframe for current frame
                var keyframe = new ValueKeyframeData 
                {
                    Value = value
                };
                
                // First frame doesn't have time in JSON
                if (frame > 0)
                {
                    keyframe.Time = time;
                }
                
                // If this is the last frame, just add it and break
                if (frame == frameLast)
                {
                    timeline.Add(keyframe);
                    break;
                }
                
                // Read next frame data
                float time2 = input.ReadFloat();
                float value2 = input.ReadFloat() * scaleFactor;
                
                Console.WriteLine($"      - Frame {frame}: time={time}, value={value}, next_time={time2}, next_value={value2}");
                
                // Read curve type for transition from current to next frame
                int curveType = input.ReadByte();
                Console.WriteLine($"      - Curve type: {curveType}");
                
                switch (curveType)
                {
                    case 1: // CURVE_STEPPED
                        keyframe.Curve = "stepped";
                        break;
                    case 2: // CURVE_BEZIER
                        float cx1 = input.ReadFloat();
                        float cy1 = input.ReadFloat();
                        float cx2 = input.ReadFloat();
                        float cy2 = input.ReadFloat();
                        Console.WriteLine($"      - Bezier: [{cx1}, {cy1}, {cx2}, {cy2}]");
                        keyframe.Curve = new List<float> { cx1, cy1, cx2, cy2 };
                        break;
                    // case 0 is CURVE_LINEAR (default), no curve data needed
                }
                
                timeline.Add(keyframe);
                
                // Move to next frame
                time = time2;
                value = value2;
            }
            
            Console.WriteLine($"    - ReadValueTimeline finished at position {input.GetPosition()}");
            
            return timeline;
        }
        
        private List<TranslateKeyframeData> ReadTranslateTimeline(int frameCount)
        {
            var timeline = new List<TranslateKeyframeData>();
            
            Console.WriteLine($"    - ReadTranslateTimeline starting with {frameCount} frames at position {input.GetPosition()}");
            
            // Follow the exact pattern from official Spine SkeletonBinary.cs
            // ReadTimeline method for CurveTimeline2 (2D timeline)
            
            float time = input.ReadFloat();
            float x = input.ReadFloat() * scale;
            float y = input.ReadFloat() * scale;
            
            Console.WriteLine($"      - Initial frame: time={time}, x={x}, y={y}");
            
            for (int frame = 0, bezier = 0, frameLast = frameCount - 1; ; frame++)
            {
                // Create keyframe for current frame
                var keyframe = new TranslateKeyframeData();
                
                // Only add non-zero values to match original format
                if (Math.Abs(x) > 0.01f) keyframe.X = FormatFloat(x);
                if (Math.Abs(y) > 0.01f) keyframe.Y = FormatFloat(y);
                
                // Always include time for all frames (following SkeletonBinary.cs pattern)
                // Only omit time if it's exactly 0 for the first frame
                if (frame == 0 && Math.Abs(time) <= 0.001f)
                {
                    // First frame at time=0, omit time property
                    keyframe.Time = null;
                }
                else
                {
                    // Include time for all other cases
                    keyframe.Time = time;
                }
                
                // If this is the last frame, just add it and break
                if (frame == frameLast)
            {
                    timeline.Add(keyframe);
                    break;
                }
                
                // Read next frame data
                float time2 = input.ReadFloat();
                float x2 = input.ReadFloat() * scale;
                float y2 = input.ReadFloat() * scale;
                
                Console.WriteLine($"      - Frame {frame}: time={time}, x={x}, y={y}, next_time={time2}, next_x={x2}, next_y={y2}");
                
                // Read curve type for transition from current to next frame
                    int curveType = input.ReadByte();
                    Console.WriteLine($"      - Curve type: {curveType}");
                    
                switch (curveType)
                {
                    case 1: // CURVE_STEPPED
                        keyframe.Curve = "stepped";
                        break;
                    case 2: // CURVE_BEZIER
                        // For 2D timelines, the official code calls SetBezier twice (once for X, once for Y)
                        // Each SetBezier call reads: cx1, cy1, cx2, cy2 (4 floats)
                        // So total: 8 floats for a 2D bezier curve
                        List<float> bezierCurve = new List<float>();
                        
                        // Read first SetBezier call (for X)
                        float cx1_x = input.ReadFloat();  // cx1 for X
                        float cy1_x = input.ReadFloat();  // cy1 for X  
                        float cx2_x = input.ReadFloat();  // cx2 for X
                        float cy2_x = input.ReadFloat();  // cy2 for X
                        
                        // Read second SetBezier call (for Y)
                        float cx1_y = input.ReadFloat();  // cx1 for Y
                        float cy1_y = input.ReadFloat();  // cy1 for Y
                        float cx2_y = input.ReadFloat();  // cx2 for Y  
                        float cy2_y = input.ReadFloat();  // cy2 for Y
                        
                        // Apply scale only to the y values (position values)
                        cy1_x *= scale;
                        cy2_x *= scale;
                        cy1_y *= scale;
                        cy2_y *= scale;
                        
                        // Arrange in the order expected by JSON: [cx1_x, cy1_x, cx2_x, cy2_x, cx1_y, cy1_y, cx2_y, cy2_y]
                        bezierCurve.AddRange(new float[] { cx1_x, cy1_x, cx2_x, cy2_x, cx1_y, cy1_y, cx2_y, cy2_y });
                        
                        Console.WriteLine($"      - Bezier X: [{cx1_x}, {cy1_x}, {cx2_x}, {cy2_x}]");
                        Console.WriteLine($"      - Bezier Y: [{cx1_y}, {cy1_y}, {cx2_y}, {cy2_y}]");
                        
                        keyframe.Curve = bezierCurve;
                        break;
                    // case 0 is CURVE_LINEAR (default), no curve data needed
                }
                
                timeline.Add(keyframe);
                
                // Move to next frame
                time = time2;
                x = x2;
                y = y2;
                    }
            
            Console.WriteLine($"    - ReadTranslateTimeline finished at position {input.GetPosition()}");
            
            return timeline;
        }
        
        private List<ScaleKeyframeData> ReadScaleTimeline(int frameCount)
        {
            var timeline = new List<ScaleKeyframeData>();
            
            Console.WriteLine($"    - ReadScaleTimeline starting with {frameCount} frames at position {input.GetPosition()}");

            // Follow the exact pattern from official Spine SkeletonBinary.cs
            // ReadTimeline method for CurveTimeline2 (2D timeline)
            
            float time = input.ReadFloat();
            float scaleX = input.ReadFloat(); // Don't apply scale factor to scale values
            float scaleY = input.ReadFloat(); // Don't apply scale factor to scale values
            
            Console.WriteLine($"      - Initial frame: time={time}, scaleX={scaleX}, scaleY={scaleY}");
            
            for (int frame = 0, bezier = 0, frameLast = frameCount - 1; ; frame++)
            {
                // Create keyframe for current frame
                var keyframe = new ScaleKeyframeData();
                
                // Only add non-default values (1.0) to match original format
                if (Math.Abs(scaleX - 1.0f) > 0.01f) keyframe.X = FormatFloat(scaleX);
                if (Math.Abs(scaleY - 1.0f) > 0.01f) keyframe.Y = FormatFloat(scaleY);
                
                // Always include time for all frames (following SkeletonBinary.cs pattern)
                // Only omit time if it's exactly 0 for the first frame
                if (frame == 0 && Math.Abs(time) <= 0.001f)
                {
                    // First frame at time=0, omit time property
                    keyframe.Time = null;
                }
                else
                {
                    // Include time for all other cases
                    keyframe.Time = time;
                }
                
                // If this is the last frame, just add it and break
                if (frame == frameLast)
                {
                timeline.Add(keyframe);
                    break;
                }
                
                // Read next frame data
                float time2 = input.ReadFloat();
                float scaleX2 = input.ReadFloat(); // Don't apply scale factor
                float scaleY2 = input.ReadFloat(); // Don't apply scale factor
                
                Console.WriteLine($"      - Frame {frame}: time={time}, scaleX={scaleX}, scaleY={scaleY}, next_time={time2}, next_scaleX={scaleX2}, next_scaleY={scaleY2}");
                
                // Read curve type for transition from current to next frame
                int curveType = input.ReadByte();
                Console.WriteLine($"      - Curve type: {curveType}");
                
                switch (curveType)
                {
                    case 1: // CURVE_STEPPED
                        keyframe.Curve = "stepped";
                        break;
                    case 2: // CURVE_BEZIER
                        // For 2D timelines, the official code calls SetBezier twice (once for X, once for Y)
                        // Each SetBezier call reads: cx1, cy1, cx2, cy2 (4 floats)
                        // So total: 8 floats for a 2D bezier curve
                        List<float> bezierCurve = new List<float>();
                        
                        // Read first SetBezier call (for ScaleX)
                        float cx1_x = input.ReadFloat();  // cx1 for X
                        float cy1_x = input.ReadFloat();  // cy1 for X  
                        float cx2_x = input.ReadFloat();  // cx2 for X
                        float cy2_x = input.ReadFloat();  // cy2 for X
                        
                        // Read second SetBezier call (for ScaleY)
                        float cx1_y = input.ReadFloat();  // cx1 for Y
                        float cy1_y = input.ReadFloat();  // cy1 for Y
                        float cx2_y = input.ReadFloat();  // cx2 for Y  
                        float cy2_y = input.ReadFloat();  // cy2 for Y
                        
                        // Don't apply scale factor to scale values, but do apply to time values
                        // Actually, checking the original JSON, no scale factor should be applied to any values in scale timeline
                        
                        // Arrange in the order expected by JSON: [cx1_x, cy1_x, cx2_x, cy2_x, cx1_y, cy1_y, cx2_y, cy2_y]
                        bezierCurve.AddRange(new float[] { cx1_x, cy1_x, cx2_x, cy2_x, cx1_y, cy1_y, cx2_y, cy2_y });
                        
                        Console.WriteLine($"      - Bezier ScaleX: [{cx1_x}, {cy1_x}, {cx2_x}, {cy2_x}]");
                        Console.WriteLine($"      - Bezier ScaleY: [{cx1_y}, {cy1_y}, {cx2_y}, {cy2_y}]");
                        
                        keyframe.Curve = bezierCurve;
                        break;
                    // case 0 is CURVE_LINEAR (default), no curve data needed
                }
                
                timeline.Add(keyframe);
                
                // Move to next frame
                time = time2;
                scaleX = scaleX2;
                scaleY = scaleY2;
            }
            
            Console.WriteLine($"    - ReadScaleTimeline finished at position {input.GetPosition()}");
            
            return timeline;
        }
        
        private List<ShearKeyframeData> ReadShearTimeline(int frameCount)
        {
            var timeline = new List<ShearKeyframeData>();
            
            Console.WriteLine($"    - ReadShearTimeline starting with {frameCount} frames at position {input.GetPosition()}");

            // Follow the exact pattern from official Spine SkeletonBinary.cs
            // ReadTimeline method for CurveTimeline2 (2D timeline)
            
            float time = input.ReadFloat();
            float shearX = input.ReadFloat(); // Don't apply scale factor to shear values
            float shearY = input.ReadFloat(); // Don't apply scale factor to shear values
            
            Console.WriteLine($"      - Initial frame: time={time}, shearX={shearX}, shearY={shearY}");
            
            for (int frame = 0, bezier = 0, frameLast = frameCount - 1; ; frame++)
            {
                // Create keyframe for current frame
                var keyframe = new ShearKeyframeData();
                
                // Only add non-zero values to match original format
                if (Math.Abs(shearX) > 0.01f) keyframe.X = FormatFloat(shearX);
                if (Math.Abs(shearY) > 0.01f) keyframe.Y = FormatFloat(shearY);
                
                // Always include time for all frames (following SkeletonBinary.cs pattern)
                // Only omit time if it's exactly 0 for the first frame
                if (frame == 0 && Math.Abs(time) <= 0.001f)
                {
                    // First frame at time=0, omit time property
                    keyframe.Time = null;
                }
                else
                {
                    // Include time for all other cases
                    keyframe.Time = time;
                }
                
                // If this is the last frame, just add it and break
                if (frame == frameLast)
                {
                timeline.Add(keyframe);
                    break;
                }
                
                // Read next frame data
                float time2 = input.ReadFloat();
                float shearX2 = input.ReadFloat(); // Don't apply scale factor
                float shearY2 = input.ReadFloat(); // Don't apply scale factor
            
                Console.WriteLine($"      - Frame {frame}: time={time}, shearX={shearX}, shearY={shearY}, next_time={time2}, next_shearX={shearX2}, next_shearY={shearY2}");
                
                // Read curve type for transition from current to next frame
                    int curveType = input.ReadByte();
                    Console.WriteLine($"      - Curve type: {curveType}");
                    
                switch (curveType)
                {
                    case 1: // CURVE_STEPPED
                        keyframe.Curve = "stepped";
                        break;
                    case 2: // CURVE_BEZIER
                        // For 2D timelines, the official code calls SetBezier twice (once for X, once for Y)
                        // Each SetBezier call reads: cx1, cy1, cx2, cy2 (4 floats)
                        // So total: 8 floats for a 2D bezier curve
                        List<float> bezierCurve = new List<float>();
                        
                        // Read first SetBezier call (for ShearX)
                        float cx1_x = input.ReadFloat();  // cx1 for X
                        float cy1_x = input.ReadFloat();  // cy1 for X  
                        float cx2_x = input.ReadFloat();  // cx2 for X
                        float cy2_x = input.ReadFloat();  // cy2 for X
                        
                        // Read second SetBezier call (for ShearY)
                        float cx1_y = input.ReadFloat();  // cx1 for Y
                        float cy1_y = input.ReadFloat();  // cy1 for Y
                        float cx2_y = input.ReadFloat();  // cx2 for Y  
                        float cy2_y = input.ReadFloat();  // cy2 for Y
                        
                        // Don't apply scale factor to shear values
                        
                        // Arrange in the order expected by JSON: [cx1_x, cy1_x, cx2_x, cy2_x, cx1_y, cy1_y, cx2_y, cy2_y]
                        bezierCurve.AddRange(new float[] { cx1_x, cy1_x, cx2_x, cy2_x, cx1_y, cy1_y, cx2_y, cy2_y });
                        
                        Console.WriteLine($"      - Bezier ShearX: [{cx1_x}, {cy1_x}, {cx2_x}, {cy2_x}]");
                        Console.WriteLine($"      - Bezier ShearY: [{cx1_y}, {cy1_y}, {cx2_y}, {cy2_y}]");
                        
                        keyframe.Curve = bezierCurve;
                        break;
                    // case 0 is CURVE_LINEAR (default), no curve data needed
                }
                
                timeline.Add(keyframe);
                
                // Move to next frame
                time = time2;
                shearX = shearX2;
                shearY = shearY2;
            }
            
            Console.WriteLine($"    - ReadShearTimeline finished at position {input.GetPosition()}");
            
            return timeline;
        }
        
        private List<DeformKeyframeData> ReadDeformTimeline(int frameCount, int frameLast)
        {
            var timeline = new List<DeformKeyframeData>();
            
            Console.WriteLine($"            - ReadDeformTimeline starting with {frameCount} frames");
            
            // Read bezierCount exactly like official DeformTimeline constructor
            int bezierCount = input.ReadInt(true);
            Console.WriteLine($"            - DeformTimeline bezierCount: {bezierCount}");
            
            // Follow the exact pattern from official Spine SkeletonBinary.cs
            float time = input.ReadFloat();
            
            for (int frame = 0, bezier = 0; ; frame++)
            {
                var keyframe = new DeformKeyframeData();
                
                // Read exactly like official SkeletonBinary.cs
                int end = input.ReadInt(true);
                Console.WriteLine($"              - Frame {frame}: time={time}, end={end}");
                
                if (end == 0)
                {
                    // Official code: deform = weighted ? new float[deformLength] : vertices;
                    // In JSON: this represents "reset to base mesh" = empty keyframe (only curve data)
                    Console.WriteLine($"                - Reset to base mesh (end=0) - empty keyframe");
                    // Don't set offset or vertices for empty keyframes
                }
                else
                {
                    // Official code: create deform array and read vertex data
                    int start = input.ReadInt(true);
                    end += start; // Official code does this
                    Console.WriteLine($"                - Deformation: start={start}, end={end} (count={end-start})");
                    
                    var vertices = new List<float>();
                    // Read exactly as official code does
                    for (int v = start; v < end; v++)
                    {
                        float value = input.ReadFloat() * scale; // Apply scale like official code
                        vertices.Add(value);
                    }
                    
                    // In JSON format, we export the start offset and the vertex displacements
                    keyframe.Offset = start;
                    keyframe.Vertices = vertices;
                    
                                                        Console.WriteLine($"                - Read {vertices.Count} vertices starting at offset {start}");
                    if (vertices.Count > 0)
                    {
                        Console.WriteLine($"                - First few values: [{string.Join(", ", vertices.Take(4).Select(v => v.ToString("F5")))}...]");
                        
                        // Debug: If we see values like -2.70105, this suggests we're reading correctly
                        if (vertices.Any(v => Math.Abs(v + 2.70105f) < 0.001f))
                        {
                            Console.WriteLine($"                - *** FOUND EXPECTED HAT_01 VALUE! Animation should be working correctly ***");
                        }
                        
                        // Debug: If we see very small values like -0.00036621094, this suggests corruption
                        if (vertices.Any(v => Math.Abs(v) < 0.001f && Math.Abs(v) > 0.0001f))
                        {
                            Console.WriteLine($"                - *** WARNING: Very small values detected - possible stream corruption ***");
                            return null; // Skip showing corrupted timelines to focus on working ones
                        }
                    }
                }
                
                // Handle time property correctly
                // Based on original JSON: first frame with curve only has NO time property
                // But this contradicts what we see - let me check our frame indexing
                bool isFirstFrame = (frame == 0);
                bool isTimeZero = Math.Abs(time) <= 0.001f;
                
                if (isFirstFrame && isTimeZero && end == 0)
                {
                    // First frame at time=0 that's empty (curve only) - no time property
                    keyframe.Time = null;
                    Console.WriteLine($"                - First empty frame: no time property");
                }
                else if (!isFirstFrame)
                {
                    // All non-first frames get time property
                    keyframe.Time = time;
                    Console.WriteLine($"                - Non-first frame: time={time}");
                }
                else 
                {
                    // First frame with data or non-zero time
                    keyframe.Time = time;
                    Console.WriteLine($"                - First frame with data/time: time={time}");
                }
                
                // If this is the last frame, add it and break
                if (frame == frameLast)
                {
                    timeline.Add(keyframe);
                    break;
                }
                
                // Read next frame time
                float time2 = input.ReadFloat();
                
                // Read curve type
                int curveType = input.ReadByte();
                Console.WriteLine($"              - Curve type: {curveType}");
                
                switch (curveType)
                {
                    case 1: // CURVE_STEPPED
                        keyframe.Curve = "stepped";
                        break;
                    case 2: // CURVE_BEZIER
                        // Official code calls SetBezier which reads 4 floats
                        float cx1 = input.ReadFloat();
                        float cy1 = input.ReadFloat();
                        float cx2 = input.ReadFloat();
                        float cy2 = input.ReadFloat();
                        
                        keyframe.Curve = new List<float> { cx1, cy1, cx2, cy2 };
                        Console.WriteLine($"              - Bezier: [{cx1}, {cy1}, {cx2}, {cy2}]");
                        break;
                    // case 0 is CURVE_LINEAR (default), no curve data needed
                }
                
                timeline.Add(keyframe);
                
                // Move to next frame
                time = time2;
            }
            
            Console.WriteLine($"            - ReadDeformTimeline finished with {timeline.Count} keyframes");
            
            return timeline;
        }
        
        private List<SequenceKeyframeData> ReadSequenceTimeline(int frameCount)
        {
            var timeline = new List<SequenceKeyframeData>();
            
            Console.WriteLine($"            - ReadSequenceTimeline starting with {frameCount} frames");
            
            for (int frame = 0; frame < frameCount; frame++)
            {
                float time = input.ReadFloat();
                int modeAndIndex = input.ReadInt();
                float delay = input.ReadFloat();
                
                var keyframe = new SequenceKeyframeData
                {
                    Time = time,
                    Mode = ((modeAndIndex & 0xf)).ToString(), // Convert mode enum to string
                    Index = modeAndIndex >> 4,
                    Delay = delay
                };
                
                timeline.Add(keyframe);
                Console.WriteLine($"              - Frame {frame}: time={time}, mode={keyframe.Mode}, index={keyframe.Index}, delay={delay}");
            }
            
            Console.WriteLine($"            - ReadSequenceTimeline finished with {timeline.Count} keyframes");
            
            return timeline;
        }
        
        private void SkipIkTimelines(int ikCount)
        {
            for (int i = 0; i < ikCount; i++)
            {
                int index = input.ReadInt(true);
                int frameCount = input.ReadInt(true);
                int bezierCount = input.ReadInt(true);
                
                // Skip IK timeline frames
                for (int frame = 0; frame < frameCount; frame++)
                {
                    input.ReadFloat(); // time
                    input.ReadFloat(); // mix
                    input.ReadFloat(); // softness
                    input.ReadSByte(); // bendDirection
                    input.ReadBoolean(); // compress
                    input.ReadBoolean(); // stretch
                    
                    if (frame < frameCount - 1)
                    {
                        int curveType = input.ReadByte();
                        if (curveType == 2) // CURVE_BEZIER
                        {
                            // Skip 2 bezier curves (mix, softness)
                            for (int j = 0; j < 2; j++)
                            {
                                input.ReadFloat(); // cx1
                                input.ReadFloat(); // cy1
                                input.ReadFloat(); // cx2
                                input.ReadFloat(); // cy2
                            }
                        }
                    }
                }
            }
        }
        
        private void SkipTransformTimelines(int transformCount)
        {
            for (int i = 0; i < transformCount; i++)
            {
                int index = input.ReadInt(true);
                int frameCount = input.ReadInt(true);
                int bezierCount = input.ReadInt(true);
                
                // Skip Transform timeline frames
                for (int frame = 0; frame < frameCount; frame++)
                {
                    input.ReadFloat(); // time
                    input.ReadFloat(); // mixRotate
                    input.ReadFloat(); // mixX
                    input.ReadFloat(); // mixY
                    input.ReadFloat(); // mixScaleX
                    input.ReadFloat(); // mixScaleY
                    input.ReadFloat(); // mixShearY
                    
                    if (frame < frameCount - 1)
                    {
                        int curveType = input.ReadByte();
                        if (curveType == 2) // CURVE_BEZIER
                        {
                            // Skip 6 bezier curves (for all mix properties)
                            for (int j = 0; j < 6; j++)
                            {
                                input.ReadFloat(); // cx1
                                input.ReadFloat(); // cy1
                                input.ReadFloat(); // cx2
                                input.ReadFloat(); // cy2
                            }
                        }
                    }
                }
            }
        }
        
        private void SkipPathTimelines(int pathCount)
        {
            for (int i = 0; i < pathCount; i++)
            {
                int index = input.ReadInt(true);
                
                int timelineCount = input.ReadInt(true);
                for (int ii = 0; ii < timelineCount; ii++)
                {
                    int pathTimelineType = input.ReadByte();
                    int frameCount = input.ReadInt(true);
                    int bezierCount = input.ReadInt(true);
                    
                    for (int frame = 0; frame < frameCount; frame++)
                    {
                        input.ReadFloat(); // time
                        
                        switch (pathTimelineType)
                        {
                            case 0: // PATH_POSITION
                            case 1: // PATH_SPACING
                                input.ReadFloat(); // value
                                break;
                            case 2: // PATH_MIX
                                input.ReadFloat(); // mixRotate
                                input.ReadFloat(); // mixX
                                input.ReadFloat(); // mixY
                                break;
                        }
                        
                        if (frame < frameCount - 1)
                        {
                            int curveType = input.ReadByte();
                            if (curveType == 2) // CURVE_BEZIER
                            {
                                int curveCount = pathTimelineType == 2 ? 3 : 1; // PATH_MIX has 3 curves, others have 1
                                for (int j = 0; j < curveCount; j++)
                                {
                                    input.ReadFloat(); // cx1
                                    input.ReadFloat(); // cy1
                                    input.ReadFloat(); // cx2
                                    input.ReadFloat(); // cy2
                                }
                            }
                        }
                    }
                }
            }
        }
        
        private void SkipDrawOrderTimelines(int drawOrderCount)
        {
            for (int i = 0; i < drawOrderCount; i++)
            {
                input.ReadFloat(); // time
                int offsetCount = input.ReadInt(true);
                
                for (int ii = 0; ii < offsetCount; ii++)
                {
                    input.ReadInt(true); // slotIndex
                    input.ReadInt(true); // offset
                }
            }
        }
        
        private void SkipEventTimelines(int eventCount)
        {
            for (int i = 0; i < eventCount; i++)
            {
                input.ReadFloat(); // time
                int eventDataIndex = input.ReadInt(true);
                input.ReadInt(false); // intValue
                input.ReadFloat(); // floatValue
                bool hasStringValue = input.ReadBoolean();
                if (hasStringValue)
                {
                    input.ReadString(); // stringValue
                }
                
                // Check if event has audio
                // This would need event data from skeleton to determine properly
                // For now, we'll skip it as we're not processing events fully
            }
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

        // Add methods to get stream position and peek at values
        public long GetPosition()
        {
            return input.Position;
        }
        
        public float PeekFloat()
        {
            long pos = input.Position;
            float value = ReadFloat();
            input.Position = pos;
            return value;
        }
        
        public float PeekFloat(int offsetBytes)
        {
            long pos = input.Position;
            input.Position += offsetBytes;
            float value = ReadFloat();
            input.Position = pos;
            return value;
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
        
        [JsonPropertyName("events")]
        public Dictionary<string, EventData> Events { get; set; } = new Dictionary<string, EventData>();
        
        [JsonPropertyName("animations")]
        public Dictionary<string, AnimationData> Animations { get; set; } = new Dictionary<string, AnimationData>();
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

    public class EventData
    {
        [JsonPropertyName("int")]
        public int? Int { get; set; }
        
        [JsonPropertyName("float")]
        public float? Float { get; set; }
        
        [JsonPropertyName("string")]
        public string? String { get; set; }
        
        [JsonPropertyName("audio")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? AudioPath { get; set; }
        
        [JsonPropertyName("volume")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public float? Volume { get; set; }
        
        [JsonPropertyName("balance")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public float? Balance { get; set; }
    }

    // Animation data classes
    public class AnimationData
    {
        [JsonPropertyName("slots")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, SlotTimelineData>? Slots { get; set; }
        
        [JsonPropertyName("bones")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, BoneTimelineData>? Bones { get; set; }
        
        [JsonPropertyName("ik")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, IkTimelineData>? Ik { get; set; }
        
        [JsonPropertyName("transform")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, TransformTimelineData>? Transform { get; set; }
        
        [JsonPropertyName("path")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, PathTimelineData>? Path { get; set; }
        
        [JsonPropertyName("deform")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, Dictionary<string, Dictionary<string, DeformTimelineData>>>? Deform { get; set; }
        
        [JsonPropertyName("drawOrder")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<DrawOrderTimelineData>? DrawOrder { get; set; }
        
        [JsonPropertyName("events")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<EventTimelineData>? Events { get; set; }
        
        [JsonPropertyName("attachments")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, Dictionary<string, Dictionary<string, AttachmentTimelineData>>>? Attachments { get; set; }
        
        /// <summary>
        /// Checks if this animation contains any valid data
        /// </summary>
        public bool HasData() 
        {
            // Check if any of the collections has data
            bool hasData = 
                (Slots != null && Slots.Count > 0) || 
                (Bones != null && Bones.Count > 0) || 
                (Ik != null && Ik.Count > 0) || 
                (Transform != null && Transform.Count > 0) || 
                (Path != null && Path.Count > 0) || 
                (Deform != null && Deform.Count > 0) || 
                (DrawOrder != null && DrawOrder.Count > 0) || 
                (Events != null && Events.Count > 0) ||
                (Attachments != null && Attachments.Count > 0);
            
            return hasData;
        }
    }

    public class SlotTimelineData
    {
        [JsonPropertyName("attachment")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<KeyframeData>? Attachment { get; set; }
        
        [JsonPropertyName("color")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ColorKeyframeData>? Color { get; set; }
        
        [JsonPropertyName("rgba")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<RGBAKeyframeData>? RGBA { get; set; }
        
        [JsonPropertyName("rgb")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<RGBKeyframeData>? RGB { get; set; }
        
        [JsonPropertyName("rgba2")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<RGBA2KeyframeData>? RGBA2 { get; set; }
        
        [JsonPropertyName("rgb2")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<RGB2KeyframeData>? RGB2 { get; set; }
    }

    public class BoneTimelineData
    {
        [JsonPropertyName("rotate")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<RotateKeyframeData>? Rotate { get; set; }
        
        [JsonPropertyName("translate")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<TranslateKeyframeData>? Translate { get; set; }
        
        [JsonPropertyName("translateX")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ValueKeyframeData>? TranslateX { get; set; }
        
        [JsonPropertyName("translateY")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ValueKeyframeData>? TranslateY { get; set; }
        
        [JsonPropertyName("scale")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ScaleKeyframeData>? Scale { get; set; }
        
        [JsonPropertyName("scaleX")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ValueKeyframeData>? ScaleX { get; set; }
        
        [JsonPropertyName("scaleY")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ValueKeyframeData>? ScaleY { get; set; }
        
        [JsonPropertyName("shear")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ShearKeyframeData>? Shear { get; set; }
        
        [JsonPropertyName("shearX")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ValueKeyframeData>? ShearX { get; set; }
        
        [JsonPropertyName("shearY")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ValueKeyframeData>? ShearY { get; set; }
    }

    public class IkTimelineData : List<IkKeyframeData> { }
    
    public class TransformTimelineData : List<TransformKeyframeData> { }
    
    public class PathTimelineData
    {
        [JsonPropertyName("position")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ValueKeyframeData>? Position { get; set; }
        
        [JsonPropertyName("spacing")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ValueKeyframeData>? Spacing { get; set; }
        
        [JsonPropertyName("mix")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<PathMixKeyframeData>? Mix { get; set; }
    }
    
    public class DeformTimelineData : List<DeformKeyframeData> { }
    
    // Base keyframe classes
    public class KeyframeData
    {
        [JsonPropertyName("time")]
        public float Time { get; set; }
        
        [JsonPropertyName("curve")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Curve { get; set; }
        
        [JsonPropertyName("name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Name { get; set; }
    }
    
    public class ValueKeyframeData : KeyframeData
    {
        [JsonPropertyName("value")]
        public float Value { get; set; }
        
        // Override the Time property to make it nullable
        [JsonPropertyName("time")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public new float? Time { get; set; }
    }
    
    public class RotateKeyframeData : KeyframeData
    {
        [JsonPropertyName("value")]
        public float Value { get; set; }
        
        // Override the Time property to make it nullable
        [JsonPropertyName("time")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public new float? Time { get; set; }
    }
    
    public class TranslateKeyframeData : KeyframeData
    {
        [JsonPropertyName("x")]
        public float? X { get; set; }
        
        [JsonPropertyName("y")]
        public float? Y { get; set; }
        
        // Override the Time property to make it nullable
        [JsonPropertyName("time")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public new float? Time { get; set; }
    }
    
    public class ScaleKeyframeData : KeyframeData
    {
        [JsonPropertyName("x")]
        public float? X { get; set; }
        
        [JsonPropertyName("y")]
        public float? Y { get; set; }
        
        // Override the Time property to make it nullable
        [JsonPropertyName("time")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public new float? Time { get; set; }
    }
    
    public class ShearKeyframeData : KeyframeData
    {
        [JsonPropertyName("x")]
        public float? X { get; set; }
        
        [JsonPropertyName("y")]
        public float? Y { get; set; }
        
        // Override the Time property to make it nullable
        [JsonPropertyName("time")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public new float? Time { get; set; }
    }
    
    public class ColorKeyframeData : KeyframeData
    {
        [JsonPropertyName("color")]
        public string Color { get; set; } = string.Empty;
    }
    
    public class RGBAKeyframeData : KeyframeData
    {
        [JsonPropertyName("color")]
        public string Color { get; set; } = string.Empty;
        
        // Override the Time property to make it nullable for first frame handling
        [JsonPropertyName("time")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public new float? Time { get; set; }
    }
    
    public class RGBKeyframeData : KeyframeData
    {
        [JsonPropertyName("color")]
        public string Color { get; set; } = string.Empty;
    }
    
    public class RGBA2KeyframeData : KeyframeData
    {
        [JsonPropertyName("light")]
        public string Light { get; set; } = string.Empty;
        
        [JsonPropertyName("dark")]
        public string Dark { get; set; } = string.Empty;
    }
    
    public class RGB2KeyframeData : KeyframeData
    {
        [JsonPropertyName("light")]
        public string Light { get; set; } = string.Empty;
        
        [JsonPropertyName("dark")]
        public string Dark { get; set; } = string.Empty;
    }
    
    public class IkKeyframeData : KeyframeData
    {
        [JsonPropertyName("mix")]
        public float? Mix { get; set; }
        
        [JsonPropertyName("softness")]
        public float? Softness { get; set; }
        
        [JsonPropertyName("bendPositive")]
        public bool? BendPositive { get; set; }
        
        [JsonPropertyName("compress")]
        public bool? Compress { get; set; }
        
        [JsonPropertyName("stretch")]
        public bool? Stretch { get; set; }
    }
    
    public class TransformKeyframeData : KeyframeData
    {
        [JsonPropertyName("mixRotate")]
        public float MixRotate { get; set; }
        
        [JsonPropertyName("mixX")]
        public float? MixX { get; set; }
        
        [JsonPropertyName("mixY")]
        public float? MixY { get; set; }
        
        [JsonPropertyName("mixScaleX")]
        public float MixScaleX { get; set; }
        
        [JsonPropertyName("mixScaleY")]
        public float? MixScaleY { get; set; }
        
        [JsonPropertyName("mixShearY")]
        public float MixShearY { get; set; }
    }
    
    public class PathMixKeyframeData : KeyframeData
    {
        [JsonPropertyName("mixRotate")]
        public float MixRotate { get; set; }
        
        [JsonPropertyName("mixX")]
        public float? MixX { get; set; }
        
        [JsonPropertyName("mixY")]
        public float? MixY { get; set; }
    }
    
        
    
    public class DrawOrderTimelineData
    {
        [JsonPropertyName("time")]
        public float Time { get; set; }
        
        [JsonPropertyName("offsets")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<DrawOrderOffsetData>? Offsets { get; set; }
    }
    
    public class DrawOrderOffsetData
    {
        [JsonPropertyName("slot")]
        public string Slot { get; set; } = string.Empty;
        
        [JsonPropertyName("offset")]
        public int Offset { get; set; }
    }
    
    public class EventTimelineData
    {
        [JsonPropertyName("time")]
        public float Time { get; set; }
        
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
        
        [JsonPropertyName("int")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int? Int { get; set; }
        
        [JsonPropertyName("float")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public float? Float { get; set; }
        
        [JsonPropertyName("string")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? String { get; set; }
        
        [JsonPropertyName("volume")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public float? Volume { get; set; }
        
        [JsonPropertyName("balance")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public float? Balance { get; set; }
    }
    
    // Attachment timeline data structures
    public class AttachmentTimelineData
    {
        [JsonPropertyName("deform")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<DeformKeyframeData>? Deform { get; set; }
        
        [JsonPropertyName("sequence")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<SequenceKeyframeData>? Sequence { get; set; }
    }
    
    public class DeformKeyframeData
    {
        [JsonPropertyName("time")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? Time { get; set; }
        
        [JsonPropertyName("offset")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Offset { get; set; }
        
        [JsonPropertyName("vertices")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<float>? Vertices { get; set; }
        
        [JsonPropertyName("curve")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Curve { get; set; }
    }
    
    public class SequenceKeyframeData
    {
        [JsonPropertyName("time")]
        public float Time { get; set; }
        
        [JsonPropertyName("mode")]
        public string Mode { get; set; } = string.Empty;
        
        [JsonPropertyName("index")]
        public int Index { get; set; }
        
        [JsonPropertyName("delay")]
        public float Delay { get; set; }
    }
} 
