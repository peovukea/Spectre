import type { Metadata } from "next";
import "./globals.css";

export const metadata: Metadata = {
  title: "Spectre Investigation",
  description: "Live semantic behavior investigation dashboard",
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="en">
      <body>{children}</body>
    </html>
  );
}
