`Dosing Line.lvlib` as it stood on 2026-11-27 after `lvai_remove_from_library` took its only `API`
member out (C:\Temp\DosingLine, LabVIEW 2026 26.3.1, project pinned to 20.0): LabVIEW wrote the
file, the tool removed the Item, and the `API` folder was left empty - the case
`LibraryTools.RemoveEmptyLibraryFolders` was written for (tests/.../LibraryRemoveTests.cs).
