local dashboardAutostart = {}

local function safeDashboardPath(value)
  local path = tostring(value or ''):gsub('[\r\n]+$', '')
  if #path < 7 or #path > 4096 then return nil end
  if path:match('^[A-Za-z]:[\\/]') == nil then return nil end
  if path:lower():match('%.exe$') == nil then return nil end
  if path:find('[\r\n"%%]') ~= nil then return nil end
  return path
end

function dashboardAutostart.launch(configPath, log)
  log = type(log) == 'function' and log or function() end
  if type(os.execute) ~= 'function' then
    log('[CrabRuntimeProbe] dashboard autostart unavailable: os.execute missing')
    return false
  end

  local file = io.open(configPath, 'r')
  if not file then
    log('[CrabRuntimeProbe] dashboard autostart not configured; run Start play guide once')
    return false
  end
  local path = safeDashboardPath(file:read('*l'))
  file:close()
  if path == nil then
    log('[CrabRuntimeProbe] dashboard autostart rejected an invalid executable path')
    return false
  end

  local ok = pcall(function()
    os.execute('start "" /b "' .. path .. '" --game-autostart')
  end)
  if ok then
    log('[CrabRuntimeProbe] dashboard autostart requested')
    return true
  end
  log('[CrabRuntimeProbe] dashboard autostart failed')
  return false
end

return dashboardAutostart
