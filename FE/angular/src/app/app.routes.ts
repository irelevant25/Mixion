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
];
