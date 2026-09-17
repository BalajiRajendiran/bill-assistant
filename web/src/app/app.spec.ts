import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { App } from './app';

describe('App', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [provideRouter([])],
    }).compileComponents();
  });

  it('creates the shell', () => {
    expect(TestBed.createComponent(App).componentInstance).toBeTruthy();
  });

  it('offers navigation to both pages', async () => {
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();

    const links = Array.from(fixture.nativeElement.querySelectorAll('nav a')).map((a) =>
      (a as HTMLElement).textContent?.trim(),
    );

    expect(links).toContain('Ask');
    expect(links).toContain('Bills');
  });
});
