import { useState } from 'react'
import { SandboxCreator, type CreationInfo } from './SandboxCreator'
import { CommandExecutor } from './CommandExecutor'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { X, Terminal } from 'lucide-react'

interface Sandbox {
  id: string
  creationInfo: CreationInfo
}

export function SandboxManager() {
  const [sandboxes, setSandboxes] = useState<Sandbox[]>([])
  const [activeTab, setActiveTab] = useState<string>('create')

  const handleSandboxCreated = (podName: string, creationInfo: CreationInfo) => {
    const newSandbox: Sandbox = {
      id: podName,
      creationInfo,
    }
    setSandboxes(prev => [...prev, newSandbox])
    setActiveTab(podName)
  }

  const handleSandboxDeleted = (sandboxId: string) => {
    setSandboxes(prev => prev.filter(s => s.id !== sandboxId))
    // Switch to create tab or first available sandbox
    if (activeTab === sandboxId) {
      const remaining = sandboxes.filter(s => s.id !== sandboxId)
      setActiveTab(remaining.length > 0 ? remaining[0].id : 'create')
    }
  }

  const closeTab = (sandboxId: string, e: React.MouseEvent) => {
    e.stopPropagation()
    handleSandboxDeleted(sandboxId)
  }

  return (
    <div className="flex flex-col h-full">
      {/* Sandbox Tabs */}
      <Tabs value={activeTab} onValueChange={setActiveTab} className="flex flex-col h-full">
        <div className="border-b border-border bg-muted/30">
          <TabsList className="h-auto p-0 bg-transparent justify-start rounded-none border-0">
            <TabsTrigger
              value="create"
              className="rounded-none border-b-2 border-transparent data-[state=active]:border-primary data-[state=active]:bg-background px-4 py-2"
            >
              + New Sandbox
            </TabsTrigger>
            {sandboxes.map(sandbox => (
              <TabsTrigger
                key={sandbox.id}
                value={sandbox.id}
                className="rounded-none border-b-2 border-transparent data-[state=active]:border-primary data-[state=active]:bg-background px-4 py-2 group"
              >
                <Terminal className="h-4 w-4 mr-2" />
                <span className="max-w-[150px] truncate" title={sandbox.id}>
                  {sandbox.id.replace('kata-sandbox-', '')}
                </span>
                <span
                  role="button"
                  tabIndex={0}
                  className="inline-flex items-center justify-center h-5 w-5 ml-2 opacity-0 group-hover:opacity-100 group-data-[state=active]:opacity-100 hover:bg-muted rounded"
                  onClick={(e) => closeTab(sandbox.id, e)}
                  onKeyDown={(e) => {
                    if (e.key === 'Enter' || e.key === ' ') {
                      closeTab(sandbox.id, e as unknown as React.MouseEvent)
                    }
                  }}
                >
                  <X className="h-3 w-3" />
                </span>
              </TabsTrigger>
            ))}
          </TabsList>
        </div>

        {/* Create Tab Content */}
        <TabsContent value="create" className="flex-1 p-4 m-0 overflow-auto">
          <div className="max-w-4xl mx-auto">
            <SandboxCreator onSandboxCreated={handleSandboxCreated} />
          </div>
        </TabsContent>

        {/* Sandbox Tab Contents */}
        {sandboxes.map(sandbox => (
          <TabsContent key={sandbox.id} value={sandbox.id} className="flex-1 p-4 m-0 overflow-auto">
            <CommandExecutor
              sandboxId={sandbox.id}
              creationInfo={sandbox.creationInfo}
              onDelete={() => handleSandboxDeleted(sandbox.id)}
            />
          </TabsContent>
        ))}
      </Tabs>
    </div>
  )
}
