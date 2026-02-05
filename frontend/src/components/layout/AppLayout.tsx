import type { ReactNode } from 'react'
import type { ActiveSection } from '@/App'
import { ThemeToggle } from './ThemeToggle'
import { Github, Box, Server } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'

interface AppLayoutProps {
  children: ReactNode
  activeSection: ActiveSection
  onSectionChange: (section: ActiveSection) => void
}

export function AppLayout({ children, activeSection, onSectionChange }: AppLayoutProps) {
  return (
    <div className="flex flex-col h-screen bg-background text-foreground">
      {/* Header */}
      <header className="border-b border-border">
        <div className="px-4 py-3 flex items-center justify-between">
          <div className="flex items-center gap-3">
            <img
              src="/donkeywork.png"
              alt="DonkeyWork Logo"
              className="w-7 h-7"
            />
            <h1 className="text-xl font-semibold hidden sm:block">Sandbox Manager</h1>
          </div>

          {/* Navigation */}
          <nav className="flex items-center gap-1">
            <Button
              variant="ghost"
              size="sm"
              onClick={() => onSectionChange('sandboxes')}
              className={cn(
                "gap-2",
                activeSection === 'sandboxes' && "bg-muted text-foreground"
              )}
            >
              <Box className="h-4 w-4" />
              <span className="hidden sm:inline">Sandboxes</span>
            </Button>
            <Button
              variant="ghost"
              size="sm"
              onClick={() => onSectionChange('mcp-servers')}
              className={cn(
                "gap-2",
                activeSection === 'mcp-servers' && "bg-muted text-foreground"
              )}
            >
              <Server className="h-4 w-4" />
              <span className="hidden sm:inline">MCP Servers</span>
            </Button>
          </nav>

          <div className="flex items-center gap-2">
            <Button
              variant="ghost"
              size="icon"
              asChild
            >
              <a
                href="https://github.com/andyjmorgan/DonkeyWork-CodeSandbox-Manager"
                target="_blank"
                rel="noopener noreferrer"
                title="View on GitHub"
              >
                <Github className="h-5 w-5" />
              </a>
            </Button>
            <ThemeToggle />
          </div>
        </div>
      </header>

      {/* Main content area */}
      <main className="flex-1 overflow-hidden">
        {children}
      </main>
    </div>
  )
}
