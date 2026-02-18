-- Bob Tick Checker - HAProxy Lua script for tick-based health checking
-- This script tracks the current tick from each Bob backend and marks
-- backends as unhealthy if they fall behind the consensus tick.

local json = require("json")

-- Configuration
local MAX_TICK_LAG = 5
local TICK_TIMEOUT_SECONDS = 30

-- Global state: tracks tick per backend
-- Format: { ["bob1"] = { tick = 12345, last_update = timestamp }, ... }
local backend_ticks = {}

-- Get the current consensus tick (highest tick across all backends)
local function get_consensus_tick()
    local max_tick = 0
    local now = core.now().sec

    for name, data in pairs(backend_ticks) do
        -- Only consider recent updates
        if (now - data.last_update) < TICK_TIMEOUT_SECONDS then
            if data.tick > max_tick then
                max_tick = data.tick
            end
        end
    end

    return max_tick
end

-- Check if a backend is healthy based on tick lag
local function is_backend_healthy(server_name)
    local data = backend_ticks[server_name]
    if not data then
        return false
    end

    local now = core.now().sec

    -- Check if tick data is stale
    if (now - data.last_update) > TICK_TIMEOUT_SECONDS then
        return false
    end

    -- Check tick lag
    local consensus = get_consensus_tick()
    if consensus == 0 then
        -- No consensus yet, assume healthy
        return true
    end

    local lag = consensus - data.tick
    return lag <= MAX_TICK_LAG
end

-- Parse tick from WebSocket message (called from response inspection)
-- This is a simplified parser - in practice you'd parse the full JSON
local function extract_tick_from_response(response_body)
    -- Look for "tick": followed by a number
    local tick_match = response_body:match('"tick"%s*:%s*(%d+)')
    if tick_match then
        return tonumber(tick_match)
    end
    return nil
end

-- HTTP health check handler
-- HAProxy calls this for each health check request
core.register_service("tick_health_check", "http", function(applet)
    local server_name = applet.headers["x-haproxy-server-name"] or "unknown"

    local healthy = is_backend_healthy(server_name)
    local consensus = get_consensus_tick()
    local data = backend_ticks[server_name] or { tick = 0, last_update = 0 }

    if healthy then
        applet:set_status(200)
        applet:add_header("Content-Type", "application/json")
        applet:start_response()
        applet:send(string.format(
            '{"status":"healthy","server":"%s","tick":%d,"consensus":%d}',
            server_name, data.tick, consensus
        ))
    else
        applet:set_status(503)
        applet:add_header("Content-Type", "application/json")
        applet:start_response()
        applet:send(string.format(
            '{"status":"unhealthy","server":"%s","tick":%d,"consensus":%d,"lag":%d}',
            server_name, data.tick, consensus, consensus - data.tick
        ))
    end
end)

-- Response filter to extract tick from Bob responses
-- This inspects WebSocket frames passing through the proxy
core.register_action("extract_tick", {"http-res"}, function(txn)
    local server_name = txn.sf:srv_name()
    if not server_name or server_name == "" then
        return
    end

    -- Try to get response body (only works for buffered responses)
    local body = txn.sf:res_body()
    if body and #body > 0 then
        local tick = extract_tick_from_response(body)
        if tick and tick > 0 then
            local now = core.now().sec
            backend_ticks[server_name] = {
                tick = tick,
                last_update = now
            }
            core.Debug(string.format("Updated tick for %s: %d", server_name, tick))
        end
    end
end, 0)

-- Agent check handler - HAProxy external agent check
-- Returns "up" or "down" based on tick health
core.register_service("tick_agent", "tcp", function(applet)
    local server_name = applet:getline():gsub("%s+", "")

    local healthy = is_backend_healthy(server_name)

    if healthy then
        applet:send("up 100%\n")
    else
        applet:send("down 0%\n")
    end
end)

-- Status endpoint for debugging
core.register_service("tick_status", "http", function(applet)
    applet:set_status(200)
    applet:add_header("Content-Type", "application/json")
    applet:start_response()

    local consensus = get_consensus_tick()
    local now = core.now().sec
    local servers = {}

    for name, data in pairs(backend_ticks) do
        local age = now - data.last_update
        local lag = consensus - data.tick
        table.insert(servers, string.format(
            '"%s":{"tick":%d,"age":%d,"lag":%d,"healthy":%s}',
            name, data.tick, age, lag, is_backend_healthy(name) and "true" or "false"
        ))
    end

    applet:send(string.format(
        '{"consensus_tick":%d,"max_lag":%d,"servers":{%s}}',
        consensus, MAX_TICK_LAG, table.concat(servers, ",")
    ))
end)

core.Info("Bob Tick Checker Lua script loaded")
