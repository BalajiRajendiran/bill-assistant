import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'chat' },
  { path: 'chat', loadComponent: () => import('./chat/chat-page').then((m) => m.ChatPage), title: 'Ask · Bill Assistant' },
  { path: 'bills', loadComponent: () => import('./bills/bills-page').then((m) => m.BillsPage), title: 'Bills · Bill Assistant' },
  { path: '**', redirectTo: 'chat' },
];
