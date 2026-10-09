# Hold one COM reference to LabVIEW for as long as this process lives.
# A LabVIEW started BY COM (LabVIEW.exe /Automation) tries to shut down when its last COM
# client disconnects, and if anything is still loaded it asks "Abort Running VIs" - a modal
# that every later COM Run then waits behind. One long-lived client keeps it from ever
# reaching that point.
param([int]$Minutes = 240)
$lv = [Activator]::CreateInstance([Type]::GetTypeFromProgID('LabVIEW.Application'))
'keeper attached'
Start-Sleep -Seconds ($Minutes * 60)
