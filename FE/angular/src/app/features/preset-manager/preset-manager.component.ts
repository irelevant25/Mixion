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
  templateUrl: './preset-manager.component.html',
  styleUrls: ['./preset-manager.component.css'],
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
