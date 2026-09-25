import type { Metadata } from 'next';
import { Geist, Geist_Mono } from 'next/font/google';
import './globals.css';

const geistSans = Geist({
  variable: '--font-geist-sans',
  subsets: ['latin'],
});

const geistMono = Geist_Mono({
  variable: '--font-geist-mono',
  subsets: ['latin'],
});

export const metadata: Metadata = {
  title: 'BIMaestro Viewer MEP',
  description: 'Explorez et simulez une maquette MEP partagée depuis Revit.',
  referrer: 'no-referrer',
  robots: { index: false, follow: false, noarchive: true },
  openGraph: {
    title: 'BIMaestro Viewer MEP',
    description: 'Consultation sécurisée d’une maquette MEP publiée depuis Revit.',
    type: 'website',
  },
  twitter: {
    card: 'summary',
    title: 'BIMaestro Viewer MEP',
    description: 'Consultation sécurisée d’une maquette MEP publiée depuis Revit.',
  },
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="fr">
      <body
        className={`${geistSans.variable} ${geistMono.variable} antialiased`}
      >
        {children}
      </body>
    </html>
  );
}
