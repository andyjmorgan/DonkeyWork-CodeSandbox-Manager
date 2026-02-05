import { useState } from 'react'
import { McpServerCreator, type McpCreationInfo } from './McpServerCreator'
import { McpServerDetail } from './McpServerDetail'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { X, Server, LayoutDashboard } from 'lucide-react'

interface McpServer {
  id: string
  creationInfo: McpCreationInfo
}

export function McpServerManager() {
  const [mcpServers, setMcpServers] = useState<McpServer[]>([])
  const [activeTab, setActiveTab] = useState<string>('create')

  const handleMcpServerCreated = (podName: string, creationInfo: McpCreationInfo) => {
    const newServer: McpServer = {
      id: podName,
      creationInfo,
    }
    setMcpServers(prev => [...prev, newServer])
    setActiveTab(podName)
  }

  const handleMcpServerDeleted = (serverId: string) => {
    setMcpServers(prev => prev.filter(s => s.id !== serverId))
    if (activeTab === serverId) {
      const remaining = mcpServers.filter(s => s.id !== serverId)
      setActiveTab(remaining.length > 0 ? remaining[0].id : 'create')
    }
  }

  const closeTab = (serverId: string, e: React.MouseEvent) => {
    e.stopPropagation()
    handleMcpServerDeleted(serverId)
  }

  return (
    <div className="flex flex-col h-full">
      <Tabs value={activeTab} onValueChange={setActiveTab} className="flex flex-col h-full">
        <div className="border-b border-border bg-muted/30">
          <TabsList className="h-auto p-0 bg-transparent justify-start rounded-none border-0">
            <TabsTrigger
              value="create"
              className="rounded-none border-b-2 border-transparent data-[state=active]:border-primary data-[state=active]:bg-background px-3 sm:px-4 py-2"
            >
              <LayoutDashboard className="h-4 w-4 sm:mr-1" />
              <span className="hidden sm:inline">Dashboard</span>
            </TabsTrigger>
            {mcpServers.map(server => (
              <TabsTrigger
                key={server.id}
                value={server.id}
                className="rounded-none border-b-2 border-transparent data-[state=active]:border-primary data-[state=active]:bg-background px-2 sm:px-4 py-2 group"
              >
                <Server className="h-4 w-4 mr-1 sm:mr-2 flex-shrink-0" />
                <span className="max-w-[60px] sm:max-w-[150px] truncate" title={server.id}>
                  {server.id.replace('kata-mcp-', '')}
                </span>
                <span
                  role="button"
                  tabIndex={0}
                  className="inline-flex items-center justify-center h-5 w-5 ml-1 sm:ml-2 flex-shrink-0 opacity-0 group-hover:opacity-100 group-data-[state=active]:opacity-100 hover:bg-muted rounded"
                  onClick={(e) => closeTab(server.id, e)}
                  onKeyDown={(e) => {
                    if (e.key === 'Enter' || e.key === ' ') {
                      closeTab(server.id, e as unknown as React.MouseEvent)
                    }
                  }}
                >
                  <X className="h-3 w-3" />
                </span>
              </TabsTrigger>
            ))}
          </TabsList>
        </div>

        <TabsContent value="create" className="flex-1 p-4 m-0 overflow-auto">
          <div className="max-w-4xl mx-auto">
            <McpServerCreator onMcpServerCreated={handleMcpServerCreated} />
          </div>
        </TabsContent>

        {mcpServers.map(server => (
          <TabsContent key={server.id} value={server.id} className="flex-1 p-4 m-0 overflow-auto">
            <McpServerDetail
              serverId={server.id}
              creationInfo={server.creationInfo}
              onDelete={() => handleMcpServerDeleted(server.id)}
            />
          </TabsContent>
        ))}
      </Tabs>
    </div>
  )
}
