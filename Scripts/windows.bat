@echo off
setlocal EnableExtensions

set "version_number=v1.3.2"
set "modpack_name=Wildcats Mod"
set "folder_name=%modpack_name%"
set "dll_name=Wildcats.dll"
set "zip_name=Wildcats Release"

set "script_dir=%~dp0"
for %%I in ("%script_dir%..") do set "repo_root=%%~fI"

set "mods_dir=%repo_root%\Mods"
set "scripts_dir=%script_dir%"
set "library_dir=%repo_root%\Library"

set "source_mod_folder=%mods_dir%\%folder_name%"
set "target_mod_folder=%scripts_dir%\%folder_name%"

set "catalog_hash_source=%library_dir%\com.unity.addressables\aa\Windows\catalog.hash"
set "catalog_json_source=%library_dir%\com.unity.addressables\aa\Windows\catalog.json"
set "settings_json_source=%library_dir%\com.unity.addressables\aa\Windows\settings.json"
set "dll_source=%library_dir%\ScriptAssemblies\%dll_name%"

set "initial_archive_name=%folder_name%.zip"
set "final_archive_name=%zip_name% %version_number% Windows.zip"

move /Y "%source_mod_folder%" "%target_mod_folder%"
move /Y "%catalog_hash_source%" "%target_mod_folder%"
move /Y "%catalog_json_source%" "%target_mod_folder%"
move /Y "%settings_json_source%" "%target_mod_folder%"
move /Y "%dll_source%" "%target_mod_folder%"

powershell -NoProfile -Command "Compress-Archive -Path '%scripts_dir%%folder_name%' -DestinationPath '%scripts_dir%%initial_archive_name%' -Force"
move /Y "%scripts_dir%%initial_archive_name%" "%scripts_dir%%final_archive_name%"
move /Y "%scripts_dir%%final_archive_name%" "%mods_dir%"
rmdir /S /Q "%target_mod_folder%"