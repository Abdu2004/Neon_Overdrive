#!/usr/bin/env bash
set -e

echo "🚀 NeonOverdrive Asset Setup (Master Zip Mode)"

# 1. Unzip the master file you uploaded
echo "📦 Unzipping neonoverdrive.zip..."
unzip -o -q neonoverdrive.zip

# 2. Create the final asset folders
mkdir -p Assets/Models/Cars Assets/Models/Tracks Assets/Models/Weapons Assets/UI Assets/Audio

# 3. Find the individual packs inside and unzip them to the right places
# We use 'find' so it works even if they are inside a subfolder
echo "📂 Sorting assets..."

find . -name "kenney_car-kit*.zip" -exec unzip -o -q {} -d Assets/Models/Cars/ \;
find . -name "kenney_racing-kit*.zip" -exec unzip -o -q {} -d Assets/Models/Tracks/ \;
find . -name "kenney_blaster-kit*.zip" -exec unzip -o -q {} -d Assets/Models/Weapons/ \;
find . -name "kenney_ui-pack*.zip" -exec unzip -o -q {} -d Assets/UI/ \;
find . -name "kenney_digital-audio*.zip" -exec unzip -o -q {} -d Assets/Audio/ \;

# 4. Cleanup the zip files to save space
echo "🧹 Cleaning up zip files..."
rm -f neonoverdrive.zip
rm -f kenney_*.zip
rm -rf neonoverdrive/ # Remove the folder if it was unzipped

# 5. Remove heavy/unused formats to keep the project light for your W530
echo "🗑️ Removing heavy formats (FBX/OBJ)..."
find Assets -type f \( -iname "*.fbx" -o -iname "*.obj" -o -iname "*.blend" -o -iname "*.max" \) -delete

echo "✅ Done! Your Assets folder is ready."
echo "👉 Check the file explorer to see your organized assets."