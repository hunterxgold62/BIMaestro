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
  title: 'BIMaestro · Maquette 3D',
  description: 'Explorez et simulez une maquette 3D partagée depuis Revit.',
  referrer: 'no-referrer',
  robots: { index: false, follow: false, noarchive: true },
  openGraph: {
    title: 'BIMaestro · Maquette 3D',
    description: 'Consultation sécurisée d’une maquette 3D publiée depuis Revit.',
    type: 'website',
  },
  twitter: {
    card: 'summary',
    title: 'BIMaestro · Maquette 3D',
    description: 'Consultation sécurisée d’une maquette 3D publiée depuis Revit.',
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
