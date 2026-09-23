import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', redirectTo: 'live', pathMatch: 'full' },
  { path: 'live', loadComponent: () => import('./live/live-wall.component').then(m => m.LiveWallComponent), title: 'Lens · Live' },
  { path: 'cameras', loadComponent: () => import('./cameras/cameras.component').then(m => m.CamerasComponent), title: 'Lens · Cameras' },
  { path: 'search', loadComponent: () => import('./search/search.component').then(m => m.SearchComponent), title: 'Lens · Search' },
  { path: 'ask', loadComponent: () => import('./ask/ask.component').then(m => m.AskComponent), title: 'Lens · Ask' },
  { path: 'jobs', loadComponent: () => import('./jobs/jobs.component').then(m => m.JobsComponent), title: 'Lens · Videos' },
  { path: '**', redirectTo: 'live' },
];
