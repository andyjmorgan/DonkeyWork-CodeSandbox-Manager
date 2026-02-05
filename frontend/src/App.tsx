import { useState } from 'react'
import { AppLayout } from '@/components/layout/AppLayout'
import { SandboxManager } from '@/components/sandbox/SandboxManager'
import { McpServerManager } from '@/components/mcp/McpServerManager'

export type ActiveSection = 'sandboxes' | 'mcp-servers'

function App() {
  const [activeSection, setActiveSection] = useState<ActiveSection>('sandboxes')

  return (
    <AppLayout activeSection={activeSection} onSectionChange={setActiveSection}>
      {activeSection === 'sandboxes' && <SandboxManager />}
      {activeSection === 'mcp-servers' && <McpServerManager />}
    </AppLayout>
  )
}

export default App
