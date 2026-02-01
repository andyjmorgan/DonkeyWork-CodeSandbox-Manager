import { useEffect, useRef, useCallback, useState } from 'react'
import { Terminal as XTerm } from '@xterm/xterm'
import { FitAddon } from '@xterm/addon-fit'
import { WebLinksAddon } from '@xterm/addon-web-links'
import '@xterm/xterm/css/xterm.css'
import { Button } from '@/components/ui/button'
import { Loader2, RefreshCw, Power, Maximize2, Minimize2 } from 'lucide-react'
import { cn } from '@/lib/utils'

interface TerminalProps {
  sandboxId: string
  className?: string
}

type ConnectionStatus = 'connecting' | 'connected' | 'disconnected' | 'error'

export function Terminal({ sandboxId, className }: TerminalProps) {
  const terminalRef = useRef<HTMLDivElement>(null)
  const xtermRef = useRef<XTerm | null>(null)
  const fitAddonRef = useRef<FitAddon | null>(null)
  const wsRef = useRef<WebSocket | null>(null)
  const connectRef = useRef<() => void>(() => {})
  const [status, setStatus] = useState<ConnectionStatus>('disconnected')
  const [errorMessage, setErrorMessage] = useState<string | null>(null)
  const [isFullscreen, setIsFullscreen] = useState(false)
  const reconnectAttemptRef = useRef(0)
  const maxReconnectAttempts = 3

  // Build WebSocket URL
  const getWebSocketUrl = useCallback(() => {
    const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:'
    const host = window.location.host
    return `${protocol}//${host}/api/kata/${sandboxId}/terminal`
  }, [sandboxId])

  // Send resize message to server
  const sendResize = useCallback((cols: number, rows: number) => {
    if (wsRef.current?.readyState === WebSocket.OPEN) {
      const message = JSON.stringify({
        type: 'resize',
        payload: { cols, rows }
      })
      wsRef.current.send(message)
    }
  }, [])

  // Handle terminal resize
  const handleResize = useCallback(() => {
    if (fitAddonRef.current && xtermRef.current) {
      fitAddonRef.current.fit()
      const { cols, rows } = xtermRef.current
      sendResize(cols, rows)
    }
  }, [sendResize])

  // Connect to WebSocket
  const connect = useCallback(() => {
    if (wsRef.current?.readyState === WebSocket.OPEN ||
        wsRef.current?.readyState === WebSocket.CONNECTING) {
      return
    }

    setStatus('connecting')
    setErrorMessage(null)

    const wsUrl = getWebSocketUrl()
    const ws = new WebSocket(wsUrl)
    ws.binaryType = 'arraybuffer'
    wsRef.current = ws

    ws.onopen = () => {
      setStatus('connected')
      reconnectAttemptRef.current = 0

      // Send initial resize after connection
      if (xtermRef.current) {
        const { cols, rows } = xtermRef.current
        sendResize(cols, rows)
      }

      // Focus the terminal
      xtermRef.current?.focus()
    }

    ws.onmessage = (event) => {
      if (xtermRef.current) {
        // Data comes as ArrayBuffer (binary)
        if (event.data instanceof ArrayBuffer) {
          const data = new Uint8Array(event.data)
          xtermRef.current.write(data)
        } else {
          // Text message
          xtermRef.current.write(event.data)
        }
      }
    }

    ws.onerror = () => {
      setErrorMessage('Connection error')
    }

    ws.onclose = (event) => {
      setStatus('disconnected')

      if (event.code !== 1000) {
        // Abnormal close - attempt reconnect
        if (reconnectAttemptRef.current < maxReconnectAttempts) {
          reconnectAttemptRef.current++
          setErrorMessage(`Connection lost. Reconnecting (${reconnectAttemptRef.current}/${maxReconnectAttempts})...`)
          setTimeout(() => connectRef.current(), 1000 * reconnectAttemptRef.current)
        } else {
          setErrorMessage('Connection lost. Click Reconnect to try again.')
          setStatus('error')
        }
      }
    }
  }, [getWebSocketUrl, sendResize])

  // Keep the ref updated with the latest connect function
  useEffect(() => {
    connectRef.current = connect
  }, [connect])

  // Disconnect WebSocket
  const disconnect = useCallback(() => {
    if (wsRef.current) {
      wsRef.current.close(1000, 'User disconnected')
      wsRef.current = null
    }
    setStatus('disconnected')
    reconnectAttemptRef.current = 0
  }, [])

  // Initialize xterm.js
  useEffect(() => {
    if (!terminalRef.current) return

    // Create terminal instance
    const xterm = new XTerm({
      cursorBlink: true,
      cursorStyle: 'block',
      fontFamily: '"JetBrains Mono", "Fira Code", "Monaco", "Menlo", "Ubuntu Mono", monospace',
      fontSize: 14,
      lineHeight: 1.2,
      theme: {
        background: '#1a1b26',
        foreground: '#a9b1d6',
        cursor: '#c0caf5',
        cursorAccent: '#1a1b26',
        selectionBackground: '#33467c',
        selectionForeground: '#c0caf5',
        black: '#32344a',
        red: '#f7768e',
        green: '#9ece6a',
        yellow: '#e0af68',
        blue: '#7aa2f7',
        magenta: '#ad8ee6',
        cyan: '#449dab',
        white: '#787c99',
        brightBlack: '#444b6a',
        brightRed: '#ff7a93',
        brightGreen: '#b9f27c',
        brightYellow: '#ff9e64',
        brightBlue: '#7da6ff',
        brightMagenta: '#bb9af7',
        brightCyan: '#0db9d7',
        brightWhite: '#acb0d0',
      },
      scrollback: 10000,
      convertEol: true,
      allowProposedApi: true,
    })

    // Create and attach addons
    const fitAddon = new FitAddon()
    const webLinksAddon = new WebLinksAddon()

    xterm.loadAddon(fitAddon)
    xterm.loadAddon(webLinksAddon)

    // Open terminal in container
    xterm.open(terminalRef.current)

    // Initial fit
    fitAddon.fit()

    // Store refs
    xtermRef.current = xterm
    fitAddonRef.current = fitAddon

    // Handle user input - send to WebSocket
    xterm.onData((data) => {
      if (wsRef.current?.readyState === WebSocket.OPEN) {
        wsRef.current.send(data)
      }
    })

    // Handle binary input (for special keys)
    xterm.onBinary((data) => {
      if (wsRef.current?.readyState === WebSocket.OPEN) {
        const buffer = new Uint8Array(data.length)
        for (let i = 0; i < data.length; i++) {
          buffer[i] = data.charCodeAt(i) & 255
        }
        wsRef.current.send(buffer)
      }
    })

    // Auto-connect (deferred to avoid synchronous setState in effect)
    const connectTimeout = setTimeout(() => connect(), 0)

    // Cleanup
    return () => {
      clearTimeout(connectTimeout)
      disconnect()
      xterm.dispose()
    }
  }, [sandboxId, connect, disconnect]) // Reconnect when sandboxId changes

  // Handle window resize
  useEffect(() => {
    const handleWindowResize = () => {
      handleResize()
    }

    window.addEventListener('resize', handleWindowResize)

    // Also observe container resize
    const resizeObserver = new ResizeObserver(() => {
      handleResize()
    })

    if (terminalRef.current) {
      resizeObserver.observe(terminalRef.current)
    }

    return () => {
      window.removeEventListener('resize', handleWindowResize)
      resizeObserver.disconnect()
    }
  }, [handleResize])

  // Toggle fullscreen
  const toggleFullscreen = () => {
    setIsFullscreen(!isFullscreen)
    // Trigger resize after state change
    setTimeout(handleResize, 100)
  }

  const statusColors: Record<ConnectionStatus, string> = {
    connecting: 'bg-yellow-500',
    connected: 'bg-green-500',
    disconnected: 'bg-gray-500',
    error: 'bg-red-500',
  }

  return (
    <div className={cn(
      "flex flex-col border border-border rounded-lg overflow-hidden bg-[#1a1b26]",
      isFullscreen && "fixed inset-4 z-50",
      className
    )}>
      {/* Terminal Header */}
      <div className="flex items-center justify-between px-3 py-2 bg-muted/50 border-b border-border">
        <div className="flex items-center gap-2">
          <span className={cn("w-2.5 h-2.5 rounded-full", statusColors[status])} />
          <span className="text-sm font-medium">
            {status === 'connecting' && 'Connecting...'}
            {status === 'connected' && 'Connected'}
            {status === 'disconnected' && 'Disconnected'}
            {status === 'error' && 'Error'}
          </span>
          {status === 'connecting' && (
            <Loader2 className="h-4 w-4 animate-spin text-muted-foreground" />
          )}
        </div>
        <div className="flex items-center gap-1">
          <Button
            variant="ghost"
            size="sm"
            onClick={toggleFullscreen}
            title={isFullscreen ? "Exit fullscreen" : "Fullscreen"}
          >
            {isFullscreen ? (
              <Minimize2 className="h-4 w-4" />
            ) : (
              <Maximize2 className="h-4 w-4" />
            )}
          </Button>
          {status === 'connected' ? (
            <Button
              variant="ghost"
              size="sm"
              onClick={disconnect}
              title="Disconnect"
            >
              <Power className="h-4 w-4" />
            </Button>
          ) : (
            <Button
              variant="ghost"
              size="sm"
              onClick={connect}
              disabled={status === 'connecting'}
              title="Reconnect"
            >
              <RefreshCw className={cn("h-4 w-4", status === 'connecting' && "animate-spin")} />
            </Button>
          )}
        </div>
      </div>

      {/* Error Message */}
      {errorMessage && (
        <div className="px-3 py-2 bg-red-500/10 text-red-500 text-sm border-b border-border">
          {errorMessage}
        </div>
      )}

      {/* Terminal Container */}
      <div
        ref={terminalRef}
        className={cn(
          "flex-1 p-2",
          isFullscreen ? "min-h-0" : "min-h-[300px]"
        )}
        onClick={() => xtermRef.current?.focus()}
      />
    </div>
  )
}
