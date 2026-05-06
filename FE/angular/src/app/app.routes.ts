import { Routes } from '@angular/router';

export const routes: Routes = [
  {
    path: '',
    loadComponent: () =>
      import('./features/routing-matrix/routing-matrix.component').then(
        (m) => m.RoutingMatrixComponent,
      ),
  },
  {
    path: 'presets',
    loadComponent: () =>
      import('./features/preset-manager/preset-manager.component').then(
        (m) => m.PresetManagerComponent,
      ),
  },
  {
    path: 'signal-flow',
    loadComponent: () =>
      import('./features/signal-flow/signal-flow.component').then(
        (m) => m.SignalFlowComponent,
      ),
  },
  {
    path: 'audio-settings',
    loadComponent: () =>
      import('./features/audio-settings/audio-settings.component').then(
        (m) => m.AudioSettingsComponent,
      ),
  },
];
