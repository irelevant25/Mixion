import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  signal,
} from '@angular/core';

import { CurrentPresetService } from '../../core/current-preset.service';
import { IpcError } from '../../core/ipc.service';
import {
  PresetActionsService,
  PresetMetadata,
} from '../../core/preset-actions.service';

/**
 * FE-040 / FE-041 / FE-042: end-to-end preset lifecycle. The presets page is
 * a list of saved presets with per-row actions (Load, Rename, Delete) and
 * timestamps; sorting defaults to "newest edited on top". Saving and Save As
 * happen from the shell header — see <c>AppComponent</c>; this page only
 * exposes the list and a "New" button that detaches the host from the
 * currently active preset.
 *
 * Confirmation dialogs use <c>window.confirm()</c> for v1 (FE-041); a custom
 * modal can replace them later if the UX gets ugly.
 */
@Component({
  selector: 'app-preset-manager',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="presets">
      <header>
        <h2>Presets</h2>
        <span class="actions-head">
          <button type="button" class="new" (click)="onNew()" [disabled]="loading() || busy()">
            New
          </button>
          <button type="button" class="refresh" (click)="refresh()" [disabled]="loading()">
            @if (loading()) { Loading… } @else { Refresh }
          </button>
        </span>
      </header>

      @if (errorMessage(); as msg) {
        <p class="error" role="alert">
          {{ msg }}
          <button type="button" class="dismiss" (click)="dismissError()">×</button>
        </p>
      }

      @if (missingDevices().length > 0) {
        <div class="warn" role="alert">
          <p class="warn-head">
            <strong>Some devices were not found</strong> when loading
            <code>{{ lastLoadedName() }}</code>:
          </p>
          <ul>
            @for (d of missingDevices(); track d.friendlyName + d.interfaceName) {
              <li>
                <span class="dir">{{ d.direction }}</span>
                <span class="friendly">{{ d.friendlyName }}</span>
                @if (d.interfaceName) {
                  <span class="iface">— {{ d.interfaceName }}</span>
                }
              </li>
            }
          </ul>
          <button type="button" class="dismiss-warn" (click)="dismissMissing()">Dismiss</button>
        </div>
      }

      @if (sortedPresets().length === 0 && !loading()) {
        <p class="empty">
          No presets saved yet. Use Save or Save As at the top of the page to
          create your first one.
        </p>
      } @else {
        <table class="preset-table">
          <colgroup>
            <col class="col-name" />
            <col class="col-ts" />
            <col class="col-ts" />
            <col class="col-actions" />
          </colgroup>
          <thead>
            <tr>
              <th scope="col">Name</th>
              <th scope="col">Created</th>
              <th scope="col">Edited</th>
              <th scope="col" class="col-actions"><span class="visually-hidden">Actions</span></th>
            </tr>
          </thead>
          <tbody>
            @for (p of sortedPresets(); track p.name) {
              <tr [class.active]="currentName() === p.name">
                <td class="cell-name">
                  <span class="name-text">{{ p.name }}</span>
                  @if (currentName() === p.name) { <em class="badge">current</em> }
                </td>
                <td class="cell-ts" [title]="p.createdAt">{{ formatDate(p.createdAt) }}</td>
                <td class="cell-ts" [title]="p.editedAt" >{{ formatDate(p.editedAt) }}</td>
                <td class="cell-actions">
                  <span class="actions">
                    <button
                      type="button"
                      (click)="onLoad(p.name)"
                      [disabled]="busyName() === p.name">
                      @if (busyName() === p.name) { … } @else { Load }
                    </button>
                    <button
                      type="button"
                      (click)="onRename(p.name)"
                      [disabled]="busyName() === p.name">Rename</button>
                    <button
                      type="button"
                      class="danger"
                      (click)="onDelete(p.name)"
                      [disabled]="busyName() === p.name">Delete</button>
                  </span>
                </td>
              </tr>
            }
          </tbody>
        </table>
      }
    </section>
  `,
  styles: [
    `
      :host { display: block; margin-top: 1.5rem; }
      .presets {
        background: #161616;
        border: 1px solid #2a2a2a;
        border-radius: 6px;
        padding: 1rem 1.1rem 1.1rem;
        max-width: 44rem;
      }
      header {
        display: flex; align-items: center; justify-content: space-between;
        margin-bottom: 0.6rem;
      }
      h2 {
        margin: 0;
        font-size: 11px;
        letter-spacing: 0.08em;
        text-transform: uppercase;
        color: #888;
      }
      .actions-head { display: flex; gap: 0.4rem; }
      .new, .refresh {
        background: transparent; border: 1px solid #333; color: #aaa;
        padding: 0.2rem 0.7rem; border-radius: 3px; cursor: pointer;
        font-size: 11px;
      }
      .new:hover:not(:disabled), .refresh:hover:not(:disabled) {
        color: #ddd; border-color: #555;
      }
      .new:disabled, .refresh:disabled { opacity: 0.4; cursor: not-allowed; }
      .empty { color: #888; font-style: italic; margin: 0.4rem 0 0; }

      /* Real <table> so the four columns track each other regardless of
         the longest preset name. fixed layout + colgroup widths keep the
         timestamp + actions columns from drifting. */
      table.preset-table {
        width: 100%;
        border-collapse: collapse;
        table-layout: fixed;
        font-size: 12px;
      }
      table.preset-table col.col-name    { width: auto; }
      table.preset-table col.col-ts      { width: 8.5rem; }
      table.preset-table col.col-actions { width: 13rem; }
      table.preset-table th {
        text-align: left;
        font-size: 10px;
        font-weight: 600;
        letter-spacing: 0.06em;
        text-transform: uppercase;
        color: #777;
        padding: 0.35rem 0.55rem;
        border-bottom: 1px solid #2a2a2a;
      }
      table.preset-table th.col-actions { text-align: right; }
      table.preset-table td {
        padding: 0.4rem 0.55rem;
        vertical-align: middle;
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
      }
      table.preset-table tbody tr:nth-child(odd) td { background: #1d1d1d; }
      table.preset-table tbody tr.active td:first-child {
        box-shadow: inset 3px 0 0 #2d6cdf;
      }
      table.preset-table .cell-name {
        font-family: ui-monospace, Menlo, Consolas, monospace;
        font-size: 12.5px;
        color: #ddd;
      }
      table.preset-table .cell-name .name-text {
        overflow: hidden; text-overflow: ellipsis;
      }
      table.preset-table .cell-name .badge {
        margin-left: 0.4rem;
        font-style: normal;
        font-size: 9.5px;
        color: #2d6cdf;
        text-transform: uppercase;
        letter-spacing: 0.08em;
      }
      table.preset-table .cell-ts {
        font-size: 11.5px;
        color: #aaa;
        font-variant-numeric: tabular-nums;
      }
      table.preset-table .cell-actions { text-align: right; }
      table.preset-table .actions {
        display: inline-flex;
        gap: 0.35rem;
        white-space: nowrap;
      }
      table.preset-table .actions button {
        background: #222; color: #ddd;
        border: 1px solid #3a3a3a; border-radius: 3px;
        padding: 0.2rem 0.6rem;
        cursor: pointer; font-size: 12px;
      }
      table.preset-table .actions button:disabled { opacity: 0.4; cursor: not-allowed; }
      table.preset-table .actions button.danger { color: #f7c8c8; border-color: #6a3030; }
      table.preset-table .actions button.danger:hover:not(:disabled) { background: #3a1f1f; }
      .visually-hidden {
        position: absolute; width: 1px; height: 1px;
        padding: 0; margin: -1px; overflow: hidden;
        clip: rect(0, 0, 0, 0); white-space: nowrap; border: 0;
      }
      .error {
        background: #3a1f1f; color: #f7c8c8;
        border: 1px solid #6a3030; border-radius: 4px;
        padding: 0.5rem 0.7rem; margin: 0 0 0.7rem;
        font-size: 12px;
        display: flex; align-items: center; justify-content: space-between; gap: 0.5rem;
      }
      .dismiss {
        background: transparent; border: 0; color: #f7c8c8;
        font-size: 16px; cursor: pointer; line-height: 1;
      }
      .warn {
        background: #2c2613; color: #f3d27a;
        border: 1px solid #6a5018; border-radius: 4px;
        padding: 0.55rem 0.75rem; margin: 0 0 0.7rem;
        font-size: 12px;
      }
      .warn .warn-head { margin: 0; }
      .warn strong { color: #f9e7b0; }
      .warn ul { margin: 0.35rem 0 0.5rem; padding-left: 1.2rem; }
      .warn li { padding: 1px 0; }
      .warn .dir {
        display: inline-block;
        min-width: 3.4em;
        font-size: 10px;
        text-transform: uppercase;
        letter-spacing: 0.06em;
        color: #c8a55b;
      }
      .warn .friendly { color: #f3d27a; }
      .warn .iface { color: #a08a52; }
      .dismiss-warn {
        background: transparent;
        border: 1px solid #6a5018;
        color: #f3d27a;
        border-radius: 3px;
        padding: 0.15rem 0.55rem;
        cursor: pointer; font-size: 11px;
      }
      .dismiss-warn:hover { background: #3a3010; }
      code {
        background: #222; padding: 1px 5px; border-radius: 3px;
        font-family: ui-monospace, Menlo, Consolas, monospace;
        font-size: 11px;
      }
    `,
  ],
})
export class PresetManagerComponent {
  private readonly actions = inject(PresetActionsService);
  private readonly current = inject(CurrentPresetService);

  protected readonly presets        = signal<PresetMetadata[]>([]);
  protected readonly loading        = signal(false);
  protected readonly busyName       = signal<string | null>(null);
  protected readonly busy           = signal(false);
  protected readonly errorMessage   = signal<string | null>(null);

  protected readonly missingDevices = this.actions.lastMissingDevices;
  protected readonly lastLoadedName = this.actions.lastLoadedName;
  protected readonly currentName    = this.current.name;
  protected readonly dirty          = this.current.dirty;

  /** Sorted by edited timestamp, newest first. */
  protected readonly sortedPresets = computed(() => {
    const rows = [...this.presets()];
    rows.sort((a, b) => Date.parse(b.editedAt) - Date.parse(a.editedAt));
    return rows;
  });

  constructor() {
    this.refresh();
  }

  protected refresh(): void {
    this.loading.set(true);
    this.actions
      .list()
      .then((rows) => this.presets.set(rows))
      .catch((err) => this.errorMessage.set(this.formatError('listPresets', err)))
      .finally(() => this.loading.set(false));
  }

  protected onNew(): void {
    if (this.dirty()) {
      const name = this.currentName();
      const message = name
        ? `You have unsaved changes to "${name}". Discard them and start a new preset?`
        : 'You have unsaved changes. Discard them and start a new preset?';
      if (!window.confirm(message)) return;
    }
    this.detach();
  }

  protected onLoad(name: string): void {
    this.errorMessage.set(null);
    this.busyName.set(name);
    this.actions
      .load(name)
      .then(() => this.refresh())
      .catch((err) => this.errorMessage.set(this.formatError('loadPreset', err)))
      .finally(() => this.busyName.set(null));
  }

  protected onRename(oldName: string): void {
    const proposal = window.prompt(`Rename preset "${oldName}" to:`, oldName);
    if (proposal === null) return;
    const newName = proposal.trim();
    if (!newName || newName === oldName) return;

    if (this.presets().some((p) => p.name === newName)) {
      this.errorMessage.set(`A preset named "${newName}" already exists.`);
      return;
    }

    this.errorMessage.set(null);
    this.busyName.set(oldName);
    this.actions
      .rename(oldName, newName)
      .then(() => this.refresh())
      .catch((err) => this.errorMessage.set(this.formatError('renamePreset', err)))
      .finally(() => this.busyName.set(null));
  }

  protected onDelete(name: string): void {
    const proceed = window.confirm(`Delete preset "${name}"? This cannot be undone.`);
    if (!proceed) return;

    this.errorMessage.set(null);
    this.busyName.set(name);
    this.actions
      .delete(name)
      .then(() => this.refresh())
      .catch((err) => this.errorMessage.set(this.formatError('deletePreset', err)))
      .finally(() => this.busyName.set(null));
  }

  protected dismissError(): void {
    this.errorMessage.set(null);
  }

  protected dismissMissing(): void {
    this.actions.clearMissingDevices();
  }

  protected formatDate(iso: string): string {
    if (!iso) return '—';
    const t = Date.parse(iso);
    if (Number.isNaN(t)) return iso;
    const d = new Date(t);
    // Locale-aware short timestamp; "edited 2 minutes ago" is overkill for v1.
    return d.toLocaleString(undefined, {
      year:   '2-digit',
      month:  '2-digit',
      day:    '2-digit',
      hour:   '2-digit',
      minute: '2-digit',
    });
  }

  /**
   * Wipe the mixer to a fresh state and detach from the active preset.
   * `PresetActionsService.newPreset` resets every channel (gain/mute/solo/DSP),
   * clears the routing matrix, and rebuilds the slot rows to defaults.
   */
  private detach(): void {
    this.errorMessage.set(null);
    this.busy.set(true);
    this.actions
      .newPreset()
      .catch((err) => this.errorMessage.set(this.formatError('resetMixerState', err)))
      .finally(() => this.busy.set(false));
  }

  private formatError(method: string, err: unknown): string {
    if (err instanceof IpcError) return `${method} failed: ${err.message} (code ${err.code})`;
    if (err instanceof Error)    return `${method} failed: ${err.message}`;
    return `${method} failed`;
  }
}
