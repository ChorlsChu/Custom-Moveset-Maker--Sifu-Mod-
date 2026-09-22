@echo off
setlocal

set UNREALPAK=C:\Users\Charles\Downloads\Sifu Modding\Unreal Pak Extracter and Creator\4.26\UE4\UnrealPak\UnrealPak.exe
set PAKDIR=C:\Users\Charles\Downloads\Sifu Modding\Unreal Pak Extracter and Creator\4.26\UE4\UnrealPak
set SRCDIR=C:\Users\Charles\Downloads\Sifu Modding\Movesets Prototypes\Moveset Maker\SifuMovesetEditor\tools\_dump_conduit\pak_test
set PAKOUT=%SRCDIR%\test_multilevel.pak

REM Copy source files to UnrealPak directory
copy "%SRCDIR%\BP_FireDisciple_ArchetypeDB_Adv_Master.uasset" "%PAKDIR%\Sifu\Content\DB\AI\Archetypes\FireDisciple\Variations\Archetype\" /Y
copy "%SRCDIR%\BP_FireDisciple_ArchetypeDB_Adv_Master.uexp" "%PAKDIR%\Sifu\Content\DB\AI\Archetypes\FireDisciple\Variations\Archetype\" /Y
copy "%SRCDIR%\FireDisciple_New_Custom_Combo.uasset" "%PAKDIR%\Sifu\Content\DB\AI\Archetypes\FireDisciple\Variations\Combo\" /Y
copy "%SRCDIR%\FireDisciple_New_Custom_Combo.uexp" "%PAKDIR%\Sifu\Content\DB\AI\Archetypes\FireDisciple\Variations\Combo\" /Y

REM Create response file in UnrealPak directory
echo "Sifu\Content\DB\AI\Archetypes\FireDisciple\Variations\Archetype\BP_FireDisciple_ArchetypeDB_Adv_Master.uasset" > "%PAKDIR%\pak_list.txt"
echo "Sifu\Content\DB\AI\Archetypes\FireDisciple\Variations\Archetype\BP_FireDisciple_ArchetypeDB_Adv_Master.uexp" >> "%PAKDIR%\pak_list.txt"
echo "Sifu\Content\DB\AI\Archetypes\FireDisciple\Variations\Combo\FireDisciple_New_Custom_Combo.uasset" >> "%PAKDIR%\pak_list.txt"
echo "Sifu\Content\DB\AI\Archetypes\FireDisciple\Variations\Combo\FireDisciple_New_Custom_Combo.uexp" >> "%PAKDIR%\pak_list.txt"

REM Create pak
"%UNREALPAK%" "%PAKOUT%" -create="%PAKDIR%\pak_list.txt" -compress

REM Clean up copied files
rmdir /s /q "%PAKDIR%\Sifu"

echo.
echo Done. Pak at: %PAKOUT%
