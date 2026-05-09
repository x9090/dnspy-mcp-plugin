dnspy-mcp-plugin — Build & Health Check

BUILD
  dotnet build dnspy-mcp-plugin.csproj
  Output: C:\tools\dnSpy-net-win64\bin\dnspy-mcp-plugin.x.dll

ONE-TIME URL ACL (run once as admin)
  netsh http add urlacl url=http://localhost:4444/ user=Everyone

RUN
  Launch dnSpyEx — the plugin loads automatically on startup.

HEALTH CHECK
  Invoke-RestMethod http://localhost:4444/health
  Expected: status=ok
